using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;

namespace MonixOne.OidcProxy.Yarp.Authentication;

// OIDC request handlers run before endpoint authorization. Select the host's
// native handler so identical callback paths cannot be claimed by another client.
internal sealed class HostAuthenticationSchemeProvider(IOptions<AuthenticationOptions> options,
    IHttpContextAccessor accessor, BffClients clients) : AuthenticationSchemeProvider(options)
{
    private readonly HashSet<string> _managed = clients.ByName.Values.Select(c => c.OpenIdConnectScheme).ToHashSet(StringComparer.Ordinal);

    public override async Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync()
    {
        var all = await base.GetRequestHandlerSchemesAsync();
        var selected = accessor.HttpContext is { } context ? clients.Find(context.Request) : null;
        return all.Where(s => !_managed.Contains(s.Name) || s.Name == selected?.OpenIdConnectScheme);
    }

    public override Task<AuthenticationScheme?> GetDefaultForbidSchemeAsync()
    {
        var context = accessor.HttpContext;
        var client = context is null ? null : clients.Find(context.Request);
        var endpoint = context?.GetEndpoint();
        var routeClient = endpoint?.Metadata.GetMetadata<RouteModel>()?.Config.Metadata?
            .GetValueOrDefault(OidcProxyDefaults.ClientMetadataKey);
        // Native authorization policies without explicit schemes must return
        // the selected BFF cookie's 403, while other host endpoints keep theirs.
        return client is not null && (routeClient == client.Name || endpoint?.Metadata.GetMetadata<BffClient>()?.Name == client.Name)
            ? GetSchemeAsync(client.CookieScheme) : base.GetDefaultForbidSchemeAsync();
    }
}
