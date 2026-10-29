using Microsoft.AspNetCore.Authentication;
using MonixOne.OidcProxy.Sessions;

namespace MonixOne.OidcProxy.Tests;

public class SessionTests
{
    [Fact]
    public async Task ConcurrentRequestsShareOneRotatingRefresh()
    {
        using var idp = new FakeIdentityProvider { RefreshRelease = new(TaskCreationOptions.RunContinuationsAsynchronously), OmitIdToken = true };
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        var owner = host.Tokens.GetActiveTicketAsync(id, default);
        await idp.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiters = Enumerable.Range(0, 200).Select(_ => host.Tokens.GetActiveTicketAsync(id, default)).ToArray();
        idp.RefreshRelease.SetResult();
        var results = await Task.WhenAll(waiters.Prepend(owner));
        Assert.Equal(1, idp.RefreshExchanges);
        Assert.All(results, ticket => Assert.Equal(idp.LastAccessToken, ticket!.Properties.GetTokenValue("access_token")));
    }

    [Fact]
    public async Task SeparateSessionsRefreshIndependently()
    {
        using var idp = new FakeIdentityProvider { RefreshRelease = new(TaskCreationOptions.RunContinuationsAsynchronously), OmitIdToken = true };
        await using var host = await BffHost.StartAsync(idp);
        var first = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        var second = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        var a = host.Tokens.GetActiveTicketAsync(first, default);
        var b = host.Tokens.GetActiveTicketAsync(second, default);
        Assert.Equal(2, idp.RefreshExchanges);
        idp.RefreshRelease.SetResult();
        Assert.All(await Task.WhenAll(a, b), Assert.NotNull);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationDoesNotLoseOwnersRotatedToken(bool cancelOwner)
    {
        using var idp = new FakeIdentityProvider { RefreshRelease = new(TaskCreationOptions.RunContinuationsAsynchronously), OmitIdToken = true };
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        using var cts = new CancellationTokenSource();
        var owner = host.Tokens.GetActiveTicketAsync(id, cancelOwner ? cts.Token : default);
        await idp.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiter = host.Tokens.GetActiveTicketAsync(id, cancelOwner ? default : cts.Token);
        cts.Cancel();
        if (!cancelOwner) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        idp.RefreshRelease.SetResult();
        Assert.NotNull(await owner);
        if (cancelOwner) Assert.NotNull(await waiter);
        Assert.NotNull(await host.Tokens.GetActiveTicketAsync(id, default));
        Assert.Equal(1, idp.RefreshExchanges);
    }

    [Fact]
    public async Task LogoutWinsAgainstLateRefreshAndNewTokenIsRevoked()
    {
        using var idp = new FakeIdentityProvider { RefreshRelease = new(TaskCreationOptions.RunContinuationsAsynchronously), OmitIdToken = true };
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        var refresh = host.Tokens.GetActiveTicketAsync(id, default);
        await idp.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Store.RemoveAsync(id, default);
        idp.RefreshRelease.SetResult();
        Assert.Null(await refresh);
        Assert.Null(await host.Store.ReadAsync(id, default));
        Assert.Equal(1, idp.Revocations);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("transport")]
    [InlineData("server_error")]
    [InlineData("missing_access")]
    [InlineData("zero_expiry")]
    [InlineData("wrong_type")]
    public async Task FailedOrAmbiguousRefreshNeverReusesOldRefreshToken(string failure)
    {
        using var idp = new FakeIdentityProvider { Failure = failure, OmitIdToken = true };
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        if (failure is "transport" or "server_error")
            await Assert.ThrowsAsync<SessionUnavailableException>(() => host.Tokens.GetActiveTicketAsync(id, default));
        else Assert.Null(await host.Tokens.GetActiveTicketAsync(id, default));
        Assert.Null(await host.Tokens.GetActiveTicketAsync(id, default));
        Assert.Equal(1, idp.RefreshExchanges);
    }

    [Fact]
    public async Task CrashedWorkerPendingMarkerCannotBeRedeemedByNextWorker()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        var snapshot = (await host.Store.ReadAsync(id, default))!;
        Assert.True(await host.Store.CompareExchangeAsync(snapshot, snapshot.State with
        {
            RefreshOperation = "crashed-worker", RefreshDeadline = host.Clock.GetUtcNow().AddSeconds(30)
        }, default));
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await host.Tokens.GetActiveTicketAsync(id, default));
        Assert.Equal(0, idp.RefreshExchanges);
    }

    [Fact]
    public async Task OmittedRefreshAndIdentityTokensPreserveExistingCredentials()
    {
        using var idp = new FakeIdentityProvider { OmitRefreshToken = true, OmitIdToken = true };
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        var before = (await host.Store.ReadAsync(id, default))!.State.ReadTicket();
        var refreshed = await host.Tokens.GetActiveTicketAsync(id, default);
        Assert.Equal(before.Properties.GetTokenValue("refresh_token"), refreshed!.Properties.GetTokenValue("refresh_token"));
        host.Clock.Advance(TimeSpan.FromSeconds(100));
        Assert.NotNull(await host.Tokens.GetActiveTicketAsync(id, default));
        Assert.Equal(2, idp.RefreshExchanges);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("missing_subject")]
    [InlineData("missing_iat")]
    [InlineData("unsigned")]
    [InlineData("azp")]
    public async Task RejectsInvalidRefreshedIdentityToken(string invalid)
    {
        using var idp = new FakeIdentityProvider { InvalidIdToken = invalid };
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        Assert.Null(await host.Tokens.GetActiveTicketAsync(id, default));
        Assert.Null(await host.Store.ReadAsync(id, default));
    }

    [Fact]
    public async Task IdleExpiryEndsSessionWithoutRefreshing()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp);
        host.Clock.Advance(TimeSpan.FromMinutes(21));
        Assert.Null(await host.Tokens.GetActiveTicketAsync(id, default));
        Assert.Equal(0, idp.RefreshExchanges);
    }

    [Fact]
    public async Task RefreshDoesNotExtendAbsoluteSessionLifetime()
    {
        using var idp = new FakeIdentityProvider { OmitIdToken = true };
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        var before = (await host.Store.ReadAsync(id, default))!.State.AbsoluteExpiresAt;
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await host.Tokens.GetActiveTicketAsync(id, default));
        Assert.Equal(before, (await host.Store.ReadAsync(id, default))!.State.AbsoluteExpiresAt);
    }

    [Fact]
    public async Task StaleSessionWriterCannotOverwriteNewTokensOrResurrectLogout()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp);
        var before = (await host.Store.ReadAsync(id, default))!;
        Assert.True(await host.Store.CompareExchangeAsync(before, before.State with { RefreshOperation = "new-state" }, default));
        Assert.False(await host.Store.CompareExchangeAsync(before, before.State, default));
        await host.Store.RemoveAsync(id, default);
        Assert.False(await host.Store.CompareExchangeAsync(before, before.State, default));
        Assert.Null(await host.Store.ReadAsync(id, default));
    }

    [Fact]
    public async Task FreshAccessTokenDoesNotCallIdentity()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp);
        Assert.NotNull(await host.Tokens.GetActiveTicketAsync(id, default));
        Assert.Equal(0, idp.RefreshExchanges);
    }

    [Fact]
    public async Task LateResponseAfterPendingDeadlineCannotBeCommitted()
    {
        using var idp = new FakeIdentityProvider { RefreshRelease = new(TaskCreationOptions.RunContinuationsAsynchronously), OmitIdToken = true };
        await using var host = await BffHost.StartAsync(idp);
        var id = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        var owner = host.Tokens.GetActiveTicketAsync(id, default);
        await idp.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await host.Tokens.GetActiveTicketAsync(id, default));
        idp.RefreshRelease.SetResult();
        Assert.Null(await owner);
        Assert.Null(await host.Store.ReadAsync(id, default));
        Assert.Equal(1, idp.RefreshExchanges);
        Assert.Equal(1, idp.Revocations);
    }

    [Fact]
    public async Task TokenWithoutOfflineAccessWorksUntilActualExpiry()
    {
        using var idp = new FakeIdentityProvider();
        await using var host = await BffHost.StartAsync(idp);
        var ticket = BffHost.CreateTicket("not-used", host.Clock.GetUtcNow().AddSeconds(5));
        ticket.Properties.StoreTokens(ticket.Properties.GetTokens().Where(t => t.Name != "refresh_token"));
        var id = await host.Store.CreateAsync(ticket, default);
        Assert.NotNull(await host.Tokens.GetActiveTicketAsync(id, default));
        host.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Null(await host.Tokens.GetActiveTicketAsync(id, default));
        Assert.Equal(0, idp.RefreshExchanges);
    }
}
