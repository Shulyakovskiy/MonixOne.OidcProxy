using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MonixOne.OidcProxy.ModuleInitializers;

namespace MonixOne.OidcProxy.OpenIdConnect;

public static class ModuleInitializer
{
    public static IServiceCollection AddOidcProxy(this IServiceCollection services, OidcProxyConfig config,
        Action<ProxyOptions>? configureOptions = null, Action<OpenIdConnectOptions>? configureOidc = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Oidc is null) throw new ArgumentException("Oidc configuration is required.");
        if (!config.Oidc.Validate(out var errors)) throw new ArgumentException(string.Join(" ", errors));
        var options = new ProxyOptions();
        config.Apply(options);
        configureOptions?.Invoke(options);
        return services.AddOidcProxy(options, oidc =>
        {
            oidc.Authority = config.Oidc.Authority;
            oidc.ClientId = config.Oidc.ClientId;
            oidc.ClientSecret = config.Oidc.ClientSecret;
            foreach (var scope in config.Oidc.Scopes)
                if (!string.IsNullOrWhiteSpace(scope)) oidc.Scope.Add(scope);
            if (config.Oidc.DisablePushedAuthorization)
                oidc.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            configureOidc?.Invoke(oidc);
        });
    }
}
