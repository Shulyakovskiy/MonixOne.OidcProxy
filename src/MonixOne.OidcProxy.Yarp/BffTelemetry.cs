using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MonixOne.OidcProxy.Yarp;

internal sealed class BffTelemetry : IDisposable
{
    private readonly ActivitySource _activities = new(OidcProxyDefaults.TelemetryName);
    private readonly Counter<long> _created;
    private readonly Counter<long> _removed;
    private readonly Counter<long> _conflicts;
    private readonly Counter<long> _refreshes;
    private readonly Counter<long> _revocationFailures;
    private readonly Counter<long> _unavailable;
    private readonly Histogram<double> _refreshDuration;
    private readonly Histogram<double> _waitDuration;

    public BffTelemetry(IMeterFactory factory)
    {
        var meter = factory.Create(OidcProxyDefaults.TelemetryName);
        _created = meter.CreateCounter<long>("oidcproxy.session.created");
        _removed = meter.CreateCounter<long>("oidcproxy.session.removed");
        _conflicts = meter.CreateCounter<long>("oidcproxy.session.cas.conflicts");
        _refreshes = meter.CreateCounter<long>("oidcproxy.refresh.completed");
        _revocationFailures = meter.CreateCounter<long>("oidcproxy.revocation.failed");
        _unavailable = meter.CreateCounter<long>("oidcproxy.session.unavailable");
        _refreshDuration = meter.CreateHistogram<double>("oidcproxy.refresh.duration", "s");
        _waitDuration = meter.CreateHistogram<double>("oidcproxy.refresh.wait.duration", "s");
    }

    internal void SessionCreated(string client) =>
        _created.Add(1, new KeyValuePair<string, object?>("client", client));

    // Counts confirmed removals observed by this process; Redis TTL expiry is excluded.
    internal void SessionRemoved(string client, long count = 1) =>
        _removed.Add(count, new KeyValuePair<string, object?>("client", client));

    internal void CasConflict(string client) =>
        _conflicts.Add(1, new KeyValuePair<string, object?>("client", client));

    internal Activity? StartRefresh(string client) =>
        _activities
            .StartActivity("oidcproxy.refresh", ActivityKind.Internal)
            ?.SetTag("client", client);

    internal void RefreshCompleted(string client, string outcome, long started)
    {
        var tags = new TagList { { "client", client }, { "outcome", outcome } };
        _refreshes.Add(1, tags);
        _refreshDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
    }

    internal void RefreshWaited(string client, long started) =>
        _waitDuration.Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            new KeyValuePair<string, object?>("client", client)
        );

    internal void RevocationFailed(string client) =>
        _revocationFailures.Add(1, new KeyValuePair<string, object?>("client", client));

    internal void SessionUnavailable(string client) =>
        _unavailable.Add(1, new KeyValuePair<string, object?>("client", client));

    public void Dispose() => _activities.Dispose();
}
