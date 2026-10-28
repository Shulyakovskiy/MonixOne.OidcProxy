using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Yarp.ReverseProxy.Configuration;

namespace MonixOne.OidcProxy.ModuleInitializers;

public sealed class ProxyOptions
{
    public string EndpointName { get; set; } = ".auth";
    public string CookieName { get; set; } = "__Host-oidcproxy.session";
    public string NameClaim { get; set; } = "name";
    public string RoleClaim { get; set; } = "role";
    public Mode Mode { get; set; }
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan SessionAbsoluteTimeout { get; set; } = TimeSpan.FromHours(8);
    public TimeSpan RefreshTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RefreshBeforeExpiration { get; set; } = TimeSpan.FromSeconds(30);
    public bool EnableUserPreferredLandingPages { get; set; }
    internal Uri? PublicOrigin { get; private set; }
    internal string LandingPage { get; private set; } = "/";
    internal string[] AllowedLandingPages { get; private set; } = [];
    internal IConnectionMultiplexer? Redis { get; private set; }
    internal string RedisKeyPrefix { get; private set; } = "monixone:oidcproxy:memory:";
    internal List<Action<IReverseProxyBuilder>> YarpConfiguration { get; } = [];
    internal string CallbackPath => $"/{EndpointName}/login/callback";

    public void SetLandingPage(string path)
    {
        if (!MonixOne.OidcProxy.LandingPage.TryParse(path, out _))
            throw new ArgumentException("Landing page must be a local absolute path.", nameof(path));
        LandingPage = path;
    }

    public void SetAllowedLandingPages(IEnumerable<string>? paths)
    {
        AllowedLandingPages = paths?.ToArray() ?? [];
        foreach (var path in AllowedLandingPages)
            if (!MonixOne.OidcProxy.LandingPage.TryParse(path, out _))
                throw new ArgumentException("Allowed landing pages must be local paths.", nameof(paths));
    }

    public void SetCustomHostName(Uri origin)
    {
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new ArgumentException("The public origin must be an HTTPS origin without a path, credentials, query or fragment.", nameof(origin));
        PublicOrigin = origin;
    }

    /// <summary>
    /// Uses a host-owned multiplexer and isolates sessions and Data Protection keys
    /// by project, environment and namespace. The host remains responsible for disposal.
    /// </summary>
    public void ConfigureRedisBackBone(ConnectionMultiplexer connectionMultiplexer,
        string projectName, string environmentName, string keyNamespace = "oidcproxy")
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        var prefix = $"{KeySegment(projectName)}:{KeySegment(environmentName)}:{KeySegment(keyNamespace)}:";
        Redis = connectionMultiplexer;
        RedisKeyPrefix = prefix;
    }

    private static string KeySegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new ArgumentException("Redis project, environment and namespace must each contain 1-64 ASCII letters, digits, dots, underscores or hyphens.");
        return value.ToLowerInvariant();
    }

    public void ConfigureYarp(Action<IReverseProxyBuilder> configure) => YarpConfiguration.Add(configure);

    public void ConfigureYarp(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
        => ConfigureYarp(builder => builder.LoadFromMemory(routes, clusters));

    internal void Validate()
    {
        if (PublicOrigin is null)
            throw new ArgumentException("SetCustomHostName must specify the public HTTPS origin. Redirect URLs are never inferred from an untrusted Host header.");
        if (string.IsNullOrWhiteSpace(EndpointName) || EndpointName.Length > 64 || EndpointName is "." or ".." ||
            EndpointName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new ArgumentException("EndpointName must be a single path segment.");
        if (string.IsNullOrWhiteSpace(CookieName) || !CookieName.StartsWith("__Host-", StringComparison.Ordinal))
            throw new ArgumentException("CookieName must start with __Host-.");
        if (string.IsNullOrWhiteSpace(RedisKeyPrefix))
            throw new ArgumentException("RedisKeyPrefix must not be empty.");
        if (SessionIdleTimeout <= TimeSpan.Zero || SessionAbsoluteTimeout < SessionIdleTimeout || SessionAbsoluteTimeout > TimeSpan.FromDays(365) ||
            RefreshTimeout < TimeSpan.FromSeconds(1) || RefreshTimeout > TimeSpan.FromMinutes(2) ||
            RefreshBeforeExpiration < TimeSpan.Zero || RefreshBeforeExpiration > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Invalid session or refresh timeouts.");
    }
}
