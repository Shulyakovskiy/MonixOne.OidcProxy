using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class RefreshLoadTests(InfrastructureFixture infrastructure, ITestOutputHelper output)
{
    [Fact]
    public async Task Refresh_HundredWaitersIncludingCancellation_CoalescesRedisReads()
    {
        // Arrange: a native cookie event is the barrier after real authentication.
        var ct = TestContext.Current.CancellationToken;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        using var identity = new OidcWireMock(infrastructure)
        { ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = identity.CreateBff();
        await using var monitored = host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<CookieAuthenticationOptions>(OidcProxyDefaults.CookieScheme("spa"), options =>
            {
                var validate = options.Events.OnValidatePrincipal;
                options.Events.OnValidatePrincipal = async context =>
                {
                    await validate(context);
                    if (!context.Request.Headers.ContainsKey("X-Test-Cohort")) return;
                    if (Interlocked.Increment(ref arrivals) == 100) ready.TrySetResult();
                    await release.Task.WaitAsync(context.HttpContext.RequestAborted);
                };
            })));
        using var browser = monitored.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = BffApplicationFactory.Origin, AllowAutoRedirect = false });
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        host.Clock.Advance(TimeSpan.FromSeconds(110));
        using var cancelledWaiter = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var owner = browser.ApiAsync(cancellationToken: ct);
            await identity.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var observerOptions = ConfigurationOptions.Parse(infrastructure.Redis.GetConnectionString());
            observerOptions.AllowAdmin = true; // INFO is used only by this test's observer.
            using var observer = await ConnectionMultiplexer.ConnectAsync(observerOptions);
            var server = observer.GetServer(observer.GetEndPoints().Single());
            var before = await ReadGetCountAsync(server, ct);

            // Act: all 100 requests are authenticated before measuring the polling rate.
            var requests = Enumerable.Range(0, 100).Select(SendCohortAsync).ToArray();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var beforePolling = await ReadGetCountAsync(server, ct);
            release.TrySetResult();
            // This interval measures command rate; request overlap uses the barriers above.
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            var duringPolling = await ReadGetCountAsync(server, ct) - beforePolling;
            var waiting = requests.Count(request => !request.IsCompleted);
            cancelledWaiter.Cancel();
            identity.ReleaseRefresh.TrySetResult();
            await Should.ThrowAsync<OperationCanceledException>(async () => await requests[0]);
            var responses = await Task.WhenAll(requests.Skip(1).Prepend(owner));
            var total = await ReadGetCountAsync(server, ct) - before;

            // Assert
            output.WriteLine($"100 waiters: GET during 500 ms = {duringPolling}; total GET = {total}; refresh = {identity.RefreshRequests}.");
            login.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            waiting.ShouldBe(100);
            responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
            identity.RefreshRequests.ShouldBe(1);
            // One CAS verification per reused authentication snapshot is expected;
            // Redis commandstats includes GET executed inside Lua as well.
            duringPolling.ShouldBeLessThanOrEqualTo(150);
            total.ShouldBeLessThanOrEqualTo(350);
            foreach (var response in responses) response.Dispose();
        }
        finally
        {
            release.TrySetResult();
            identity.ReleaseRefresh.TrySetResult();
        }

        async Task<HttpResponseMessage> SendCohortAsync(int index)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
            request.Headers.Add("X-CSRF", "1");
            request.Headers.Add("X-Test-Cohort", "1");
            return await browser.SendAsync(request, index == 0 ? cancelledWaiter.Token : ct);
        }
    }

    private static async Task<long> ReadGetCountAsync(IServer server, CancellationToken ct)
    {
        var info = await server.InfoAsync("commandstats").WaitAsync(ct);
        var entry = info.SelectMany(group => group).Single(pair => pair.Key == "cmdstat_get").Value;
        return long.Parse(entry.Split(',')[0]["calls=".Length..]);
    }
}
