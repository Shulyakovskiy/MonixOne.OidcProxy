using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MonixOne.OidcProxy.ModuleInitializers;
using MonixOne.OidcProxy.OpenIdConnect;

namespace MonixOne.OidcProxy.Auth0;

public static class ModuleInitializer
{
    public static IServiceCollection AddAuth0Proxy(this IServiceCollection services, Auth0ProxyConfig config,
        Action<ProxyOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Auth0 is null || Uri.CheckHostName(config.Auth0.Domain) != UriHostNameType.Dns)
            throw new ArgumentException("A valid Auth0 domain is required.", nameof(config));
        var oidc = new OpenIdConnectConfig
        {
            Authority = "https://" + config.Auth0.Domain,
            ClientId = config.Auth0.ClientId,
            ClientSecret = config.Auth0.ClientSecret,
            Scopes = config.Auth0.Scopes
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
            auth.Events.OnRedirectToIdentityProvider = context =>
            {
                if (!string.IsNullOrEmpty(config.Auth0.Audience))
                    context.ProtocolMessage.SetParameter("audience", config.Auth0.Audience);
                return Task.CompletedTask;
            };
        });
    }

    public static WebApplication UseAuth0Proxy(this WebApplication app) => app.UseOidcProxy();
}
