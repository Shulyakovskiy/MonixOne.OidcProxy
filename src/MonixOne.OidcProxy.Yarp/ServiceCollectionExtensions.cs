using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
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
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Composes native OIDC, Redis sessions and YARP for multiple SPA origins.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures YARP, Redis, trusted forwarded headers and all BFF clients
    /// from the host configuration. Uses the host's environment name.
    /// </summary>
    public static IServiceCollection AddOidcProxy(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IProxyStateLookup)))
            throw new InvalidOperationException(
                "AddOidcProxy(configuration) must be the single YARP registration."
            );

        // Freeze BFF settings and the Redis configuration key for this host.
        var snapshot = new ConfigurationBuilder()
            .AddInMemoryCollection(
                configuration.GetSection("Bff").AsEnumerable(makePathsRelative: true)
            )
            .Build();
        var clients = snapshot
            .GetSection("Clients")
            .GetChildren()
            .Where(client =>
                client.GetSection(nameof(OidcProxyClientOptions.PublicOrigin)).Exists()
            )
            .ToArray();
        var connectionInterface = services.LastOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IConnectionMultiplexer)
        );
        if (connectionInterface?.ImplementationInstance is ConnectionMultiplexer existingConnection)
            services.TryAddSingleton(existingConnection);
        else
            services.TryAddSingleton(sp =>
            {
                if (connectionInterface is not null)
                    return sp.GetRequiredService<IConnectionMultiplexer>() as ConnectionMultiplexer
                        ?? throw new InvalidOperationException(
                            "OidcProxy requires the existing IConnectionMultiplexer to be a ConnectionMultiplexer."
                        );
                var key = sp.GetRequiredService<
                    IOptions<ProxyOptions>
                >().Value.RedisConnectionStringKey;
                var connection = configuration[key];
                if (string.IsNullOrWhiteSpace(connection))
                    throw new InvalidOperationException(
                        $"Redis configuration '{key}' is required."
                    );
                return ConnectionMultiplexer.Connect(connection);
            });
        AddRedisAlias(services);
        services.AddReverseProxy().LoadFromConfig(configuration.GetRequiredSection("ReverseProxy"));
        services.AddCors(options =>
            options.AddPolicy(
                OidcProxyDefaults.CorsPolicy,
                policy =>
                    policy
                        .WithOrigins(configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials()
                        .WithExposedHeaders(GatewayTraceTransformProvider.CorrelationHeader)
            )
        );
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            var forwarded = configuration.GetSection("ForwardedHeaders");
            if (forwarded.GetValue<bool>("TrustAllProxies"))
            {
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                options.ForwardLimit = 1;
            }
            else
            {
                foreach (var proxy in forwarded.GetSection("KnownProxies").Get<string[]>() ?? [])
                    options.KnownProxies.Add(IPAddress.Parse(proxy));
                foreach (
                    var network in forwarded.GetSection("KnownIPNetworks").Get<string[]>() ?? []
                )
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
            foreach (var host in forwarded.GetSection("AllowedHosts").Get<string[]>() ?? [])
                options.AllowedHosts.Add(host);
        });
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITransformProvider, GatewayTraceTransformProvider>()
        );
        if (clients.Length == 0)
        {
            services.AddOptions<ProxyOptions>().Configure(snapshot.Bind);
            services.AddAuthentication();
            services.AddAuthorization();
        }
        else
        {
            services.AddOidcProxy(snapshot.Bind);
            foreach (var client in clients)
            {
                var oidcConfiguration = client.GetRequiredSection("OpenIdConnect");
                services.AddOidcProxyClient(
                    client.Key,
                    options =>
                    {
                        options.AutoBindRoutes = !client
                            .GetSection(nameof(OidcProxyClientOptions.Routes))
                            .Exists();
                        client.Bind(options);
                    },
                    oidc =>
                    {
                        // Explicit scopes replace the native openid/profile defaults.
                        if (
                            oidcConfiguration
                                .GetSection(nameof(OpenIdConnectOptions.Scope))
                                .Exists()
                        )
                            oidc.Scope.Clear();
                        oidcConfiguration.Bind(oidc);
                    }
                );
            }
        }
        services
            .AddOptions<ProxyOptions>()
            .Configure<IHostEnvironment>(
                (options, environment) => options.EnvironmentName = environment.EnvironmentName
            )
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.RedisConnectionStringKey),
                "Bff:RedisConnectionStringKey must name a configuration value."
            )
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Registers shared infrastructure after YARP and a host-owned ConnectionMultiplexer.
    /// </summary>
    public static IServiceCollection AddOidcProxy(
        this IServiceCollection services,
        Action<ProxyOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (services.All(d => d.ServiceType != typeof(IProxyStateLookup)))
            throw new InvalidOperationException("Register AddReverseProxy before AddOidcProxy.");
        if (services.All(d => d.ServiceType != typeof(ConnectionMultiplexer)))
            throw new InvalidOperationException(
                "Register one host-owned ConnectionMultiplexer before AddOidcProxy."
            );
        if (services.Any(d => d.ServiceType == typeof(BffClients)))
            throw new InvalidOperationException(
                "Register shared AddOidcProxy once, then AddOidcProxyClient for each SPA."
            );
        var concrete = services.Last(d => d.ServiceType == typeof(ConnectionMultiplexer));
        var alias = services.LastOrDefault(d => d.ServiceType == typeof(IConnectionMultiplexer));
        if (
            concrete.ImplementationInstance is { } connection
            && alias?.ImplementationInstance is { } other
            && !ReferenceEquals(connection, other)
        )
            throw new InvalidOperationException(
                "ConnectionMultiplexer and IConnectionMultiplexer must resolve to the same shared connection."
            );
        AddRedisAlias(services);
        services
            .AddOptions<ProxyOptions>()
            .Configure(configure)
            .Validate(
                o =>
                    ProxyOptions.IsKeySegment(o.ApplicationName)
                    && ProxyOptions.IsKeySegment(o.EnvironmentName)
                    && ProxyOptions.IsKeySegment(o.KeyNamespace),
                "ApplicationName, EnvironmentName and KeyNamespace require 1-64 ASCII letters, digits, dots, underscores or hyphens."
            )
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.EncryptionKey),
                "Bff:EncryptionKey is required and must contain a strong random secret."
            )
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITransformProvider, GatewayTraceTransformProvider>()
        );
        services.AddSingleton<BffClients>();
        services.AddHttpContextAccessor();
        services.AddAuthentication();
        var existing = services.Last(d => d.ServiceType == typeof(IAuthenticationSchemeProvider));
        if (existing.ImplementationType != typeof(AuthenticationSchemeProvider))
            throw new InvalidOperationException(
                "OidcProxy requires the standard AuthenticationSchemeProvider."
            );
        services.Replace(
            ServiceDescriptor.Singleton<
                IAuthenticationSchemeProvider,
                HostAuthenticationSchemeProvider
            >()
        );
        RedisSessionRegistration.Register(services);
        BffRequestMiddleware.Register(services);
        services.AddSingleton<BffTransformProvider>();
        services.AddSingleton<ITransformProvider>(sp =>
            sp.GetRequiredService<BffTransformProvider>()
        );
        services.AddSingleton<IProxyConfigFilter>(sp =>
            sp.GetRequiredService<BffTransformProvider>()
        );
        return services;
    }

    private static void AddRedisAlias(IServiceCollection services)
    {
        var registration = services.Last(d => d.ServiceType == typeof(ConnectionMultiplexer));
        if (registration.ImplementationInstance is ConnectionMultiplexer connection)
            services.TryAddSingleton<IConnectionMultiplexer>(connection);
        else
            services.TryAddSingleton<IConnectionMultiplexer>(sp =>
                sp.GetRequiredService<ConnectionMultiplexer>()
            );
    }

    /// <summary>
    /// Registers one named confidential OIDC client on a distinct HTTPS origin.
    /// </summary>
    public static IServiceCollection AddOidcProxyClient(
        this IServiceCollection services,
        string name,
        Action<OidcProxyClientOptions> configureClient,
        Action<OpenIdConnectOptions> configureOidc
    )
    {
        ArgumentNullException.ThrowIfNull(configureClient);
        ArgumentNullException.ThrowIfNull(configureOidc);
        if (!ProxyOptions.IsKeySegment(name) || name != name.ToLowerInvariant())
            throw new ArgumentException(
                "Client names require 1-64 lowercase ASCII letters, digits, dots, underscores or hyphens.",
                nameof(name)
            );
        if (services.All(d => d.ServiceType != typeof(BffClients)))
            throw new InvalidOperationException("Register AddOidcProxy before AddOidcProxyClient.");
        if (
            services.Any(d =>
                d.ImplementationInstance is RegisteredBffClient client && client.Name == name
            )
        )
            throw new InvalidOperationException($"BFF client '{name}' is already registered.");
        services.AddSingleton(new RegisteredBffClient(name));
        services
            .AddOptions<OidcProxyClientOptions>(name)
            .Configure(configureClient)
            .Validate(
                o => o.HasValidOrigin,
                "PublicOrigin must be an HTTPS origin without path, credentials, query or fragment."
            )
            .Validate(
                o => o.HasValidPathBase,
                "PathBase must be empty or a canonical URI-escaped path without a trailing slash."
            )
            .Validate(
                o =>
                    o.Routes.All(pair =>
                        !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null
                    ),
                "Routes require nonempty YARP RouteIds and BFF route options."
            )
            .Validate(
                o =>
                    o.CookiePolicies.All(pair =>
                        ProxyOptions.IsKeySegment(pair.Key)
                        && pair.Key == pair.Key.ToLowerInvariant()
                        && pair.Value is not null
                        && pair.Value.IsValid(o.PublicOrigin)
                    ),
                "CookiePolicies require lowercase policy names, a ClusterId, valid cookie names/prefixes, a canonical path and valid SameSite/MaxAge."
            )
            .Validate(
                o =>
                    o.SessionIdleTimeout > TimeSpan.Zero
                    && o.SessionAbsoluteTimeout >= o.SessionIdleTimeout
                    && o.SessionAbsoluteTimeout <= TimeSpan.FromDays(365),
                "SessionAbsoluteTimeout must be between SessionIdleTimeout and 365 days; both must be positive."
            )
            .Validate(
                o =>
                    o.RememberedSessionTimeout > TimeSpan.Zero
                    && o.RememberedSessionTimeout <= TimeSpan.FromDays(365),
                "RememberedSessionTimeout must be positive and at most 365 days."
            )
            .ValidateOnStart();
        services.AddKeyedSingleton<SessionStore>(
            name,
            (sp, _) =>
                new SessionStore(
                    sp.GetRequiredService<BffClients>().ByName[name],
                    sp.GetRequiredService<ConnectionMultiplexer>(),
                    sp.GetRequiredService<IOptions<ProxyOptions>>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IHostApplicationLifetime>()
                )
        );
        services.AddKeyedSingleton<TokenSession>(
            name,
            (sp, _) =>
                new TokenSession(
                    sp.GetRequiredKeyedService<SessionStore>(name),
                    sp.GetRequiredService<BffClients>().ByName[name],
                    sp.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IHostApplicationLifetime>(),
                    sp.GetRequiredService<ILogger<TokenSession>>()
                )
        );
        OidcAuthentication.Register(services, name, configureOidc);
        return services;
    }
}
