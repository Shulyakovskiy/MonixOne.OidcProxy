using MonixOne.OidcProxy.ModuleInitializers;

namespace MonixOne.OidcProxy;

public class ProxyConfig
{
    public Mode Mode { get; set; }
    public bool ProtectOnlyMarkedRoutes { get; set; }
    public string EndpointName { get; set; } = ".auth";
    public string? LandingPage { get; set; }
    public string? NameClaim { get; set; }
    public string? RoleClaim { get; set; }
    public IEnumerable<string> AllowedLandingPages { get; set; } = [];
    public bool EnableUserPreferredLandingPages { get; set; }
    public Uri? CustomHostName { get; set; }
    public string? CookieName { get; set; }
    public TimeSpan? SessionIdleTimeout { get; set; }
    public TimeSpan? SessionAbsoluteTimeout { get; set; }
    public YarpConfig? ReverseProxy { get; set; }

    public virtual void Apply(ProxyOptions options)
    {
        options.Mode = Mode;
        options.ProtectOnlyMarkedRoutes = ProtectOnlyMarkedRoutes;
        options.EndpointName = EndpointName;
        options.EnableUserPreferredLandingPages = EnableUserPreferredLandingPages;
        options.SetAllowedLandingPages(AllowedLandingPages);
        if (LandingPage is not null) options.SetLandingPage(LandingPage);
        if (CustomHostName is not null) options.SetCustomHostName(CustomHostName);
        if (CookieName is not null) options.CookieName = CookieName;
        if (NameClaim is not null) options.NameClaim = NameClaim;
        if (RoleClaim is not null) options.RoleClaim = RoleClaim;
        if (SessionIdleTimeout is { } idle) options.SessionIdleTimeout = idle;
        if (SessionAbsoluteTimeout is { } absolute) options.SessionAbsoluteTimeout = absolute;
        if (ReverseProxy is not null)
            options.ConfigureYarp(ReverseProxy.Routes.ToRouteConfig(), ReverseProxy.Clusters.ToClusterConfig());
    }
}
