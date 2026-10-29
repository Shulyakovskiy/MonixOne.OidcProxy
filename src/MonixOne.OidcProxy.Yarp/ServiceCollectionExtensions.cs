using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonixOne.OidcProxy.Yarp.Authentication;
using MonixOne.OidcProxy.Yarp.Forwarding;
using MonixOne.OidcProxy.Yarp.Security;
using MonixOne.OidcProxy.Yarp.Sessions;
using StackExchange.Redis;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Composes native OIDC, Redis sessions and YARP for multiple SPA origins.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds shared settings and automatically registers every named client from
    /// the supplied BFF configuration section. Uses the host's environment name.
    /// </summary>
    public static IServiceCollection AddOidcProxy(this IServiceCollection services, IConfigurationSection configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var clients = configuration.GetRequiredSection("Clients").GetChildren().ToArray();
        if (clients.Length == 0)
            throw new InvalidOperationException($"{configuration.Path}:Clients must contain at least one SPA client.");
        services.AddOidcProxy(options => configuration.Bind(options));
        services.AddOptions<ProxyOptions>().Configure<IHostEnvironment>((options, environment) =>
            options.EnvironmentName = environment.EnvironmentName);
        foreach (var client in clients)
        {
            var oidcConfiguration = client.GetRequiredSection("OpenIdConnect");
            services.AddOidcProxyClient(client.Key, options => client.Bind(options), oidc =>
            {
                // ConfigurationBinder appends to preinitialized collections. An
                // explicit scope list must replace the native openid/profile defaults.
                if (oidcConfiguration.GetSection(nameof(OpenIdConnectOptions.Scope)).Exists()) oidc.Scope.Clear();
                oidcConfiguration.Bind(oidc);
            });
        }
        return services;
    }

    /// <summary>
    /// Registers shared infrastructure after YARP and a host-owned ConnectionMultiplexer.
    /// </summary>
    public static IServiceCollection AddOidcProxy(this IServiceCollection services, Action<ProxyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (services.All(d => d.ServiceType != typeof(IProxyStateLookup)))
            throw new InvalidOperationException("Register AddReverseProxy before AddOidcProxy.");
        if (services.All(d => d.ServiceType != typeof(ConnectionMultiplexer)))
            throw new InvalidOperationException("Register one host-owned ConnectionMultiplexer before AddOidcProxy.");
        if (services.Any(d => d.ServiceType == typeof(BffClients)))
            throw new InvalidOperationException("Register shared AddOidcProxy once, then AddOidcProxyClient for each SPA.");
        services.AddOptions<ProxyOptions>().Configure(configure)
            .Validate(o => ProxyOptions.IsKeySegment(o.ApplicationName) && ProxyOptions.IsKeySegment(o.EnvironmentName) && ProxyOptions.IsKeySegment(o.KeyNamespace),
                "ApplicationName, EnvironmentName and KeyNamespace require 1-64 ASCII letters, digits, dots, underscores or hyphens.")
            .ValidateOnStart();
        services.AddSingleton<BffClients>();
        services.AddHttpContextAccessor();
        services.AddAuthentication();
        var existing = services.Last(d => d.ServiceType == typeof(IAuthenticationSchemeProvider));
        if (existing.ImplementationType != typeof(AuthenticationSchemeProvider))
            throw new InvalidOperationException("OidcProxy requires the standard AuthenticationSchemeProvider.");
        services.Replace(ServiceDescriptor.Singleton<IAuthenticationSchemeProvider, HostAuthenticationSchemeProvider>());
        RedisSessionRegistration.Register(services);
        BffRequestMiddleware.Register(services);
        services.AddSingleton<ITransformProvider, BffTransformProvider>();
        return services;
    }

    /// <summary>
    /// Registers one named confidential OIDC client on a distinct HTTPS origin.
    /// </summary>
    public static IServiceCollection AddOidcProxyClient(this IServiceCollection services, string name,
        Action<OidcProxyClientOptions> configureClient, Action<OpenIdConnectOptions> configureOidc)
    {
        ArgumentNullException.ThrowIfNull(configureClient);
        ArgumentNullException.ThrowIfNull(configureOidc);
        if (!ProxyOptions.IsKeySegment(name) || name != name.ToLowerInvariant())
            throw new ArgumentException("Client names require 1-64 lowercase ASCII letters, digits, dots, underscores or hyphens.", nameof(name));
        if (services.All(d => d.ServiceType != typeof(BffClients)))
            throw new InvalidOperationException("Register AddOidcProxy before AddOidcProxyClient.");
        if (services.Any(d => d.ImplementationInstance is RegisteredBffClient client && client.Name == name))
            throw new InvalidOperationException($"BFF client '{name}' is already registered.");
        services.AddSingleton(new RegisteredBffClient(name));
        services.AddOptions<OidcProxyClientOptions>(name).Configure(configureClient)
            .Validate(o => o.HasValidOrigin, "PublicOrigin must be an HTTPS origin without path, credentials, query or fragment.")
            .Validate(o => o.HasValidPathBase, "PathBase must be empty or a canonical URI-escaped path without a trailing slash.")
            .Validate(o => o.CookiePolicies is not null && o.CookiePolicies.All(pair => ProxyOptions.IsKeySegment(pair.Key) && pair.Key == pair.Key.ToLowerInvariant() && pair.Value is not null && pair.Value.IsValid(o.PublicOrigin)),
                "CookiePolicies require lowercase policy names, a ClusterId, valid cookie names/prefixes, a canonical path and valid SameSite/MaxAge.")
            .Validate(o => o.SessionIdleTimeout > TimeSpan.Zero && o.SessionAbsoluteTimeout >= o.SessionIdleTimeout && o.SessionAbsoluteTimeout <= TimeSpan.FromDays(365),
                "SessionAbsoluteTimeout must be between SessionIdleTimeout and 365 days; both must be positive.")
            .ValidateOnStart();
        services.AddKeyedSingleton<SessionStore>(name, (sp, _) => new SessionStore(sp.GetRequiredService<BffClients>().ByName[name],
            sp.GetRequiredService<ConnectionMultiplexer>(), sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddKeyedSingleton<TokenSession>(name, (sp, _) => new TokenSession(
            sp.GetRequiredKeyedService<SessionStore>(name), sp.GetRequiredService<BffClients>().ByName[name],
            sp.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetRequiredService<ILogger<TokenSession>>()));
        OidcAuthentication.Register(services, name, configureOidc);
        return services;
    }
}
