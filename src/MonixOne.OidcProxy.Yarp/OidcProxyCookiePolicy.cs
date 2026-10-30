using Microsoft.AspNetCore.Http;
using CookieHeaderValue = Microsoft.Net.Http.Headers.CookieHeaderValue;

namespace MonixOne.OidcProxy.Yarp;

/// <summary>
/// Allows selected application cookies on explicitly bound YARP routes.
/// Browser names are isolated by BFF client and policy; values remain opaque.
/// </summary>
public sealed class OidcProxyCookiePolicy
{
    /// <summary>
    /// The only YARP cluster to which this policy may forward cookies.
    /// </summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>
    /// Allowed backend cookie names. A trailing * matches a nonempty prefix,
    /// for example .AspNetCore.Correlation.* for dynamic correlation names.
    /// </summary>
    public string[] Names { get; set; } = [];

    /// <summary>
    /// Server-side forwarding path relative to the client's PathBase, for example
    /// /calendar-api/connections/google/callback. Browser cookies use Path=/
    /// to meet the __Host- prefix requirements.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Minimum SameSite protection. A stricter backend value is retained.
    /// Set None explicitly for cross-site POST callbacks whose backend also emits None.
    /// </summary>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;

    /// <summary>
    /// Optional upper bound for an existing persistent expiry. Session cookies
    /// remain session cookies; this setting never extends the backend lifetime.
    /// </summary>
    public TimeSpan? MaxAge { get; set; }

    internal bool Matches(string name) =>
        Names.Any(pattern =>
            pattern.EndsWith('*')
                ? name.StartsWith(pattern[..^1], StringComparison.Ordinal)
                : name == pattern
        );

    internal bool IsValid(Uri? origin)
    {
        if (
            string.IsNullOrWhiteSpace(ClusterId)
            || Names is not { Length: > 0 }
            || !Names.All(IsValidPattern)
        )
            return false;
        if (
            string.IsNullOrEmpty(Path)
            || !Path.StartsWith('/')
            || !Uri.TryCreate(origin, Path, out var uri)
            || uri.AbsolutePath != Path
        )
            return false;
        return SameSite is SameSiteMode.None or SameSiteMode.Lax or SameSiteMode.Strict
            && (MaxAge is null || MaxAge > TimeSpan.Zero && MaxAge <= TimeSpan.FromDays(365));
    }

    private static bool IsValidPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;
        var name = pattern.EndsWith('*') ? pattern[..^1] : pattern;
        return name.Length > 0
            && !name.Contains('*')
            && CookieHeaderValue.TryParse(name + "=value", out var parsed)
            && parsed.Name.ToString() == name;
    }
}
