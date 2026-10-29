using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MonixOne.OidcProxy.Yarp.Authentication;
using MonixOne.OidcProxy.Yarp.Forwarding;
using MonixOne.OidcProxy.Yarp.Security;
using MonixOne.OidcProxy.Yarp.Sessions;
using StackExchange.Redis;
using Yarp.ReverseProxy.Transforms.Builder;
using Yarp.ReverseProxy;

namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Composes standard OIDC, Redis session and YARP modules for a SPA BFF.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the BFF after the host has registered YARP and one
    /// ConnectionMultiplexer. Both configuration delegates use standard options.
    /// </summary>
    public static IServiceCollection AddOidcProxy(this IServiceCollection services,
        Action<ProxyOptions> configureBff, Action<OpenIdConnectOptions> configureOidc)
    {
        ArgumentNullException.ThrowIfNull(configureBff);
        ArgumentNullException.ThrowIfNull(configureOidc);
        if (services.All(d => d.ServiceType != typeof(IProxyStateLookup)))
            throw new InvalidOperationException("Register AddReverseProxy before AddOidcProxy.");
        if (services.All(d => d.ServiceType != typeof(ConnectionMultiplexer)))
            throw new InvalidOperationException("Register one host-owned ConnectionMultiplexer before AddOidcProxy.");
        if (services.Any(d => d.ServiceType == typeof(SessionStore)))
            throw new InvalidOperationException("Register one OidcProxy per application host.");

        services.AddOptions<ProxyOptions>().Configure(configureBff)
            .Validate(o => o.HasValidOrigin, "PublicOrigin must be an HTTPS origin without path, credentials, query or fragment.")
            .Validate(o => ProxyOptions.IsKeySegment(o.ApplicationName) && ProxyOptions.IsKeySegment(o.EnvironmentName) && ProxyOptions.IsKeySegment(o.KeyNamespace),
                "ApplicationName, EnvironmentName and KeyNamespace require 1-64 ASCII letters, digits, dots, underscores or hyphens.")
            .Validate(o => o.SessionIdleTimeout > TimeSpan.Zero && o.SessionAbsoluteTimeout >= o.SessionIdleTimeout && o.SessionAbsoluteTimeout <= TimeSpan.FromDays(365),
                "SessionAbsoluteTimeout must be between SessionIdleTimeout and 365 days; both must be positive.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ProxyOptions>>().Value);
        RedisSessionRegistration.Register(services);
        OidcAuthentication.Register(services, configureOidc);
        BffRequestMiddleware.Register(services);
        services.AddSingleton<ITransformProvider, BffTransformProvider>();
        return services;
    }
}
