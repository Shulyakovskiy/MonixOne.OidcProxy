using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace MonixOne.OidcProxy.Yarp;

internal sealed record RegisteredBffClient(string Name);

internal sealed record BffClient(string Name, OidcProxyClientOptions Options, string RedisKeyPrefix)
{
    internal string CookieScheme => OidcProxyDefaults.CookieScheme(Name);
    internal string OpenIdConnectScheme => OidcProxyDefaults.OpenIdConnectScheme(Name);
}

internal sealed record BffRoute(BffClient Client, OidcProxyRouteOptions Options);

internal sealed class BffClients
{
    internal FrozenDictionary<string, BffClient> ByName { get; }
    internal FrozenDictionary<string, BffRoute> ByRoute { get; }
    private readonly FrozenDictionary<string, BffClient> _byHost;

    public BffClients(
        IEnumerable<RegisteredBffClient> registrations,
        IOptionsMonitor<OidcProxyClientOptions> options,
        IOptions<ProxyOptions> host
    )
    {
        ByName = registrations
            .Select(r => new BffClient(
                r.Name,
                options.Get(r.Name),
                host.Value.RedisKeyPrefix + r.Name + ":"
            ))
            .ToFrozenDictionary(c => c.Name, StringComparer.Ordinal);
        if (ByName.Count == 0)
            throw new InvalidOperationException("Register at least one AddOidcProxyClient.");
        // Host and effective HTTPS port form the origin; implicit and explicit 443 are equivalent.
        var collision = ByName
            .Values.GroupBy(
                c => Key(c.Options.PublicOrigin!.IdnHost, c.Options.PublicOrigin.Port),
                StringComparer.OrdinalIgnoreCase
            )
            .FirstOrDefault(group => group.Skip(1).Any());
        if (collision is not null)
            throw new InvalidOperationException(
                $"BFF clients {string.Join(", ", collision.Select(c => c.Name))} must use distinct PublicOrigin hosts/ports."
            );
        _byHost = ByName.Values.ToFrozenDictionary(
            c => Key(c.Options.PublicOrigin!.IdnHost, c.Options.PublicOrigin.Port),
            StringComparer.OrdinalIgnoreCase
        );
        var routes = ByName
            .Values.SelectMany(client =>
                client.Options.Routes.Select(route => new KeyValuePair<string, BffRoute>(
                    route.Key,
                    new(client, route.Value)
                ))
            )
            .ToArray();
        var duplicate = routes
            .GroupBy(route => route.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"BFF route '{duplicate.Key}' must belong to exactly one client."
            );
        ByRoute = routes.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal BffClient? Find(HttpRequest request) =>
        request.IsHttps
        && _byHost.TryGetValue(Key(request.Host.Host, request.Host.Port ?? 443), out var client)
            ? client
            : null;

    internal BffRoute? Find(RouteConfig route)
    {
        if (ByRoute.TryGetValue(route.RouteId, out var binding))
            return binding;
        if (route.Match.Hosts is not { Count: > 0 } hosts)
            return null;
        var client = ByName.Values.FirstOrDefault(candidate =>
            candidate.Options.AutoBindRoutes
            && hosts.All(host =>
                string.Equals(
                    host,
                    candidate.Options.PublicOrigin!.Authority,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        );
        return client is null
            ? null
            : new BffRoute(
                client,
                new OidcProxyRouteOptions
                {
                    AllowAnonymous = string.Equals(
                        route.AuthorizationPolicy,
                        "anonymous",
                        StringComparison.OrdinalIgnoreCase
                    ),
                }
            );
    }

    private static string Key(string host, int port) => host + ":" + port;
}
