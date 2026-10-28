using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MonixOne.OidcProxy.ModuleInitializers;
using MonixOne.OidcProxy.OpenIdConnect;

namespace MonixOne.OidcProxy.EntraId;

public static class ModuleInitializer
{
    public static IServiceCollection AddEntraIdProxy(this IServiceCollection services, EntraIdProxyConfig config,
        Action<ProxyOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.EntraId is null || !Guid.TryParse(config.EntraId.TenantId, out var tenant))
            throw new ArgumentException("A concrete Entra tenant GUID is required; common/multi-tenant issuers need an explicit issuer policy.");
        var oidc = new OpenIdConnectConfig
        {
            Authority = $"https://login.microsoftonline.com/{tenant}/v2.0",
            ClientId = config.EntraId.ClientId,
            ClientSecret = config.EntraId.ClientSecret,
            Scopes = config.EntraId.Scopes
        };
        if (!oidc.Validate(out var errors)) throw new ArgumentException(string.Join(" ", errors));
        var options = new ProxyOptions();
        config.Apply(options);
        configureOptions?.Invoke(options);
        return services.AddOidcProxy(options, auth =>
        {
            auth.Authority = oidc.Authority;
            auth.ClientId = oidc.ClientId;
            auth.ClientSecret = oidc.ClientSecret;
            foreach (var scope in oidc.Scopes) auth.Scope.Add(scope);
        });
    }

    public static WebApplication UseEntraIdProxy(this WebApplication app) => app.UseOidcProxy();
}
