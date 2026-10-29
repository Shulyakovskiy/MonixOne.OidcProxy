using System.Net;
using Microsoft.AspNetCore.Authentication;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace MonixOne.OidcProxy.Tests;

[CollectionDefinition("Redis", DisableParallelization = true)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>;

public sealed class RedisFixture : IAsyncLifetime
{
    internal RedisContainer Container { get; } = new RedisBuilder("redis:8-alpine").Build();
    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();

    internal async Task<ConnectionMultiplexer> ConnectAsync()
    {
        var options = ConfigurationOptions.Parse(Container.GetConnectionString());
        options.AsyncTimeout = 1000;
        options.ConnectTimeout = 1000;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }
}

[Collection("Redis")]
public class RedisConcurrencyTests(RedisFixture fixture)
{
    [Fact]
    public async Task IndependentReplicasAndHttpRequestsRefreshExactlyOneTime()
    {
        using var idp = new FakeIdentityProvider { RefreshRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var firstConnection = await fixture.ConnectAsync();
        using var secondConnection = await fixture.ConnectAsync();
        var keyNamespace = Guid.NewGuid().ToString("N");
        var clock = new TestClock();
        await using var first = await BffHost.StartAsync(idp, firstConnection, keyNamespace, clock);
        await using var second = await BffHost.StartAsync(idp, secondConnection, keyNamespace, clock);
        var browser = new Browser(first);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync(idp)).StatusCode);
        var cookie = browser.Cookies;
        clock.Advance(TimeSpan.FromSeconds(100));
        var other = new Browser(second);
        var tasks = Enumerable.Range(0, 100).Select(i => (i % 2 == 0 ? browser : other)
            .SendAsync(HttpMethod.Get, "/api/data", cookies: cookie)).ToArray();
        await idp.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        idp.RefreshRelease.SetResult();
        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, idp.RefreshExchanges);
        Assert.All(responses, r => Assert.Contains("no-store", r.Headers.CacheControl!.ToString()));
    }

    [Fact]
    public async Task RestartedReplicaReadsProtectedSessionAndDataProtectionKeys()
    {
        using var idp = new FakeIdentityProvider();
        using var connection = await fixture.ConnectAsync();
        var keyNamespace = Guid.NewGuid().ToString("N");
        string cookie;
        await using (var original = await BffHost.StartAsync(idp, connection, keyNamespace))
        {
            var browser = new Browser(original);
            await browser.LoginAsync(idp);
            cookie = browser.Cookies;
        }
        await using var restarted = await BffHost.StartAsync(idp, connection, keyNamespace);
        Assert.Equal(HttpStatusCode.OK, (await new Browser(restarted)
            .SendAsync(HttpMethod.Get, "/api/data", cookies: cookie)).StatusCode);
        Assert.True(connection.IsConnected); // The host never disposes the supplied multiplexer.
    }

    [Theory]
    [InlineData("other-project", "test", false)]
    [InlineData("test-project", "production", false)]
    [InlineData("test-project", "test", true)]
    public async Task ProjectEnvironmentAndNamespaceIsolateBothCookieKeysAndSessionRecords(
        string project, string environment, bool otherNamespace)
    {
        using var idp = new FakeIdentityProvider();
        using var connection = await fixture.ConnectAsync();
        var ns = Guid.NewGuid().ToString("N");
        await using var first = await BffHost.StartAsync(idp, connection, ns);
        await using var isolated = await BffHost.StartAsync(idp, connection,
            otherNamespace ? Guid.NewGuid().ToString("N") : ns, project: project, environment: environment);
        var browser = new Browser(first);
        await browser.LoginAsync(idp);
        var id = await first.SeedSessionAsync(idp);
        Assert.Null(await isolated.Store.ReadAsync(id, default));
        Assert.Equal(HttpStatusCode.Unauthorized, (await new Browser(isolated)
            .SendAsync(HttpMethod.Get, "/api/data", cookies: browser.Cookies)).StatusCode);
    }

    [Fact]
    public async Task RedisContainsOnlyProtectedTicketsAndNamespacedExpiringKeys()
    {
        using var idp = new FakeIdentityProvider();
        using var connection = await fixture.ConnectAsync();
        var ns = Guid.NewGuid().ToString("N");
        await using var host = await BffHost.StartAsync(idp, connection, ns);
        var id = await host.SeedSessionAsync(idp);
        var key = $"test-project:test:{ns}:sessions:{id}";
        var db = connection.GetDatabase();
        var value = (await db.StringGetAsync(key)).ToString();
        Assert.NotEmpty(value);
        Assert.DoesNotContain("initial-opaque-access", value);
        Assert.DoesNotContain("refresh_token", value);
        Assert.DoesNotContain("user-1", value);
        Assert.InRange((await db.KeyTimeToLiveAsync(key))!.Value, TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(20));
        Assert.True(await db.KeyExistsAsync($"test-project:test:{ns}:data-protection"));
        await db.StringSetAsync(key, "tampered");
        Assert.Null(await host.Store.ReadAsync(id, default));
        Assert.False(await db.KeyExistsAsync(key));
    }

    [Fact]
    public async Task LogoutOnAnotherReplicaPreventsLateRefreshFromResurrectingSession()
    {
        using var idp = new FakeIdentityProvider { RefreshRelease = new(TaskCreationOptions.RunContinuationsAsynchronously), OmitIdToken = true };
        using var a = await fixture.ConnectAsync();
        using var b = await fixture.ConnectAsync();
        var ns = Guid.NewGuid().ToString("N");
        await using var first = await BffHost.StartAsync(idp, a, ns);
        await using var second = await BffHost.StartAsync(idp, b, ns);
        var id = await first.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        var owner = first.Tokens.GetActiveTicketAsync(id, default);
        await idp.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await second.Store.RemoveAsync(id, default);
        idp.RefreshRelease.SetResult();
        Assert.Null(await owner);
        Assert.Null(await first.Store.ReadAsync(id, default));
        Assert.Null(await second.Store.ReadAsync(id, default));
        Assert.Equal(1, idp.RefreshExchanges);
        Assert.Equal(1, idp.Revocations);
    }

    [Fact]
    public async Task RedisOutageReturns503AndUnpersistedRotationCannotBeRetriedAfterRecovery()
    {
        using var idp = new FakeIdentityProvider { RefreshRelease = new(TaskCreationOptions.RunContinuationsAsynchronously), OmitIdToken = true };
        using var connection = await fixture.ConnectAsync();
        var ns = Guid.NewGuid().ToString("N");
        await using var host = await BffHost.StartAsync(idp, connection, ns);
        var browser = new Browser(host);
        await browser.LoginAsync(idp);
        var id = await host.SeedSessionAsync(idp, TimeSpan.FromSeconds(-1));
        var refresh = host.Tokens.GetActiveTicketAsync(id, default);
        await idp.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Container.StopAsync();
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                (await browser.SendAsync(HttpMethod.Get, "/api/data")).StatusCode);
            idp.RefreshRelease.SetResult();
            await Assert.ThrowsAsync<MonixOne.OidcProxy.Sessions.SessionUnavailableException>(() => refresh);
        }
        finally
        {
            await fixture.Container.StartAsync();
        }
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        using var recoveredConnection = await fixture.ConnectAsync();
        await using var recovered = await BffHost.StartAsync(idp, recoveredConnection, ns, host.Clock);
        Assert.Null(await recovered.Tokens.GetActiveTicketAsync(id, default));
        Assert.Equal(1, idp.RefreshExchanges);
    }

    [Theory]
    [InlineData("")]
    [InlineData("project:other")]
    [InlineData("{project}")]
    [InlineData(" project")]
    [InlineData("project*")]
    public async Task RedisScopeSegmentsCannotEscapeTheirNamespace(string segment)
    {
        using var connection = await fixture.ConnectAsync();
        var options = new MonixOne.OidcProxy.ModuleInitializers.ProxyOptions();
        Assert.Throws<ArgumentException>(() => options.ConfigureRedisBackBone(connection, segment, "test"));
        Assert.Throws<ArgumentException>(() => options.ConfigureRedisBackBone(connection, "project", segment));
        Assert.Throws<ArgumentException>(() => options.ConfigureRedisBackBone(connection, "project", "test", segment));
    }
}
