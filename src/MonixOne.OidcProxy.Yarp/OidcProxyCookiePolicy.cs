using Microsoft.Net.Http.Headers;

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
    /// Cookie path relative to the client's PathBase, for example /calendar-api/connections/google/callback.
    /// </summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>
    /// Minimum SameSite protection. A stricter backend value is retained.
    /// Set None explicitly for cross-site POST callbacks whose backend also emits None.
    /// </summary>
    public Microsoft.AspNetCore.Http.SameSiteMode SameSite { get; set; } = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
    /// <summary>
    /// Optional upper bound for an existing persistent expiry. Session cookies
    /// remain session cookies; this setting never extends the backend lifetime.
    /// </summary>
    public TimeSpan? MaxAge { get; set; }

    internal bool Matches(string name) => Names.Any(pattern => pattern.EndsWith('*')
        ? name.StartsWith(pattern[..^1], StringComparison.Ordinal)
        : name == pattern);

    internal bool IsValid(Uri? origin) => !string.IsNullOrWhiteSpace(ClusterId) && Names is { Length: > 0 } &&
        Names.All(IsValidPattern) && Names.Distinct(StringComparer.Ordinal).Count() == Names.Length &&
        !string.IsNullOrEmpty(Path) && Path.StartsWith('/') && !Path.StartsWith("//", StringComparison.Ordinal) &&
        Uri.TryCreate(origin, Path, out var uri) && uri.AbsolutePath == Path && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        SameSite is Microsoft.AspNetCore.Http.SameSiteMode.None or Microsoft.AspNetCore.Http.SameSiteMode.Lax or Microsoft.AspNetCore.Http.SameSiteMode.Strict &&
        (MaxAge is null || MaxAge > TimeSpan.Zero && MaxAge <= TimeSpan.FromDays(365));

    private static bool IsValidPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var name = pattern.EndsWith('*') ? pattern[..^1] : pattern;
        return name.Length > 0 && !name.Contains('*') && CookieHeaderValue.TryParse(name + "=value", out var parsed) &&
            parsed.Name.ToString() == name;
    }
}
