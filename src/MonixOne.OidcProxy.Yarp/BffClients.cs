using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace MonixOne.OidcProxy.Yarp;

internal sealed record RegisteredBffClient(string Name);
internal sealed record BffClient(string Name, OidcProxyClientOptions Options, string RedisKeyPrefix)
{
    internal string CookieScheme => OidcProxyDefaults.CookieScheme(Name);
    internal string OpenIdConnectScheme => OidcProxyDefaults.OpenIdConnectScheme(Name);
    internal string CookieName => OidcProxyDefaults.CookieName(Name);
    internal bool Matches(HttpRequest request) => request.IsHttps &&
        string.Equals(request.Host.Host, Options.PublicOrigin!.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        (request.Host.Port ?? 443) == Options.PublicOrigin.Port;
}

internal sealed class BffClients
{
    internal FrozenDictionary<string, BffClient> ByName { get; }
    private readonly FrozenDictionary<string, BffClient> _byHost;
    public BffClients(IEnumerable<RegisteredBffClient> registrations, IOptionsMonitor<OidcProxyClientOptions> options,
        IOptions<ProxyOptions> host)
    {
        ByName = registrations.Select(r => new BffClient(r.Name, options.Get(r.Name), host.Value.RedisKeyPrefix + r.Name + ":"))
            .ToFrozenDictionary(c => c.Name, StringComparer.Ordinal);
        if (ByName.Count == 0) throw new InvalidOperationException("Register at least one AddOidcProxyClient.");
        // Host and effective HTTPS port form the origin; implicit and explicit 443 are equivalent.
        var collision = ByName.Values.GroupBy(c => Key(c.Options.PublicOrigin!.IdnHost, c.Options.PublicOrigin.Port),
            StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Skip(1).Any());
        if (collision is not null)
            throw new InvalidOperationException($"BFF clients {string.Join(", ", collision.Select(c => c.Name))} must use distinct PublicOrigin hosts/ports.");
        _byHost = ByName.Values.ToFrozenDictionary(c => Key(c.Options.PublicOrigin!.IdnHost, c.Options.PublicOrigin.Port),
            StringComparer.OrdinalIgnoreCase);
    }
    internal BffClient? Find(HttpRequest request) => request.IsHttps &&
        _byHost.TryGetValue(Key(request.Host.Host, request.Host.Port ?? 443), out var client) ? client : null;
    private static string Key(string host, int port) => host + ":" + port;
}
