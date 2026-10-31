using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace MonixOne.OidcProxy.Yarp.Tests;

[Collection("Infrastructure")]
public sealed class TelemetryTests(InfrastructureFixture infrastructure)
{
    [Theory]
    [InlineData(null, "success", HttpStatusCode.OK)]
    [InlineData("invalid_grant", "reauthentication_required", HttpStatusCode.Unauthorized)]
    [InlineData("503", "provider_failure", HttpStatusCode.ServiceUnavailable)]
    public async Task Refresh_Outcome_EmitsMetricsAndTraceWithoutCredentials(
        string? failure,
        string outcome,
        HttpStatusCode expected
    )
    {
        // Arrange: listen to this host's Meter only; no exporter or telemetry backend is needed.
        var ct = TestContext.Current.CancellationToken;
        using var identity = new OidcWireMock(infrastructure) { TokenFailure = failure };
        await using var host = identity.CreateBff();
        var meter = host
            .Services.GetRequiredService<IMeterFactory>()
            .Create(OidcProxyDefaults.TelemetryName);
        var counters =
            new ConcurrentQueue<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        var timings = new ConcurrentQueue<double>();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, target) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
                target.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) =>
                counters.Enqueue((instrument.Name, value, tags.ToArray()))
        );
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => timings.Enqueue(value));
        listener.Start();
        using var tracing = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OidcProxyDefaults.TelemetryName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(tracing);
        using var browser = host.CreateBrowser();
        using var provider = identity.CreateIdentityBrowser();
        using var login = await browser.LoginAsync(provider);
        host.Clock.Advance(TimeSpan.FromSeconds(110));

        // Act
        using var response = await browser.ApiAsync(cancellationToken: ct);

        // Assert
        response.StatusCode.ShouldBe(expected);
        var completed = counters.Where(m => m.Name == "oidcproxy.refresh.completed").ToArray();
        completed.Length.ShouldBe(1);
        completed[0].Value.ShouldBe(1);
        completed[0].Tags.ShouldContain(t => t.Key == "outcome" && Equals(t.Value, outcome));
        counters.ShouldContain(m => m.Name == "oidcproxy.session.created");
        if (expected == HttpStatusCode.ServiceUnavailable)
            counters.ShouldContain(m => m.Name == "oidcproxy.session.unavailable");
        counters.SelectMany(m => m.Tags).ShouldAllBe(t => t.Key == "client" || t.Key == "outcome");
        timings.ShouldNotBeEmpty();
        timings.ShouldAllBe(value => value >= 0);
        var refresh = activities.Single(a => a.OperationName == "oidcproxy.refresh");
        refresh.Status.ShouldBe(
            failure is null ? ActivityStatusCode.Unset : ActivityStatusCode.Error
        );
        refresh.TagObjects.ShouldAllBe(tag => tag.Key == "client");
    }
}
