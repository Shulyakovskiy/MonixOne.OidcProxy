using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

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
}
