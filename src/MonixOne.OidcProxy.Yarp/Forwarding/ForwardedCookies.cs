using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Transforms;
using HeaderSameSite = Microsoft.Net.Http.Headers.SameSiteMode;

namespace MonixOne.OidcProxy.Yarp.Forwarding;

// This is header isolation, not an authentication protocol: the backend receives
// its original names and opaque values and still validates its own state/nonce.
internal static class ForwardedCookies
{
    internal static bool ApplyRequest(
        RequestTransformContext context,
        BffClient client,
        string policyName,
        OidcProxyCookiePolicy policy
    )
    {
        var path = context.HttpContext.Request.Path.ToUriComponent();
        if (!path.StartsWith(policy.Path, StringComparison.Ordinal))
            return true;
        if (
            path.Length > policy.Path.Length
            && !policy.Path.EndsWith('/')
            && path[policy.Path.Length] != '/'
        )
            return true;
        var raw = context.HttpContext.Request.Headers.Cookie;
        if (raw.Count == 0)
            return true;
        if (
            !CookieHeaderValue.TryParseStrictList(
                raw.Select(value => value!).ToArray(),
                out var cookies
            )
        )
            return false;
        var prefix = Prefix(client, policyName);
        var selected = new List<CookieHeaderValue>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cookie in cookies)
        {
            var name = cookie.Name.ToString();
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var original = name[prefix.Length..];
            if (!policy.Matches(original))
                continue;
            if (!names.Add(original))
                return false;
            selected.Add(new CookieHeaderValue(original, cookie.Value));
        }
        if (selected.Count > 0)
            context.ProxyRequest.Headers.TryAddWithoutValidation(
                HeaderNames.Cookie,
                string.Join("; ", selected)
            );
        return true;
    }

    internal static bool ApplyResponse(
        ResponseTransformContext context,
        BffClient client,
        string policyName,
        OidcProxyCookiePolicy policy,
        DateTimeOffset now
    )
    {
        if (context.ProxyResponse?.Headers.TryGetValues(HeaderNames.SetCookie, out var raw) != true)
            return true;
        if (!SetCookieHeaderValue.TryParseStrictList(raw!.ToArray(), out var cookies))
            return false;
        var prefix = Prefix(client, policyName);
        var minimum = policy.SameSite switch
        {
            Microsoft.AspNetCore.Http.SameSiteMode.None => HeaderSameSite.None,
            Microsoft.AspNetCore.Http.SameSiteMode.Strict => HeaderSameSite.Strict,
            _ => HeaderSameSite.Lax,
        };
        foreach (var cookie in cookies.Where(cookie => policy.Matches(cookie.Name.ToString())))
        {
            var sameSite =
                cookie.SameSite == HeaderSameSite.Unspecified
                    ? HeaderSameSite.Lax
                    : cookie.SameSite;
            var forwarded = new SetCookieHeaderValue(prefix + cookie.Name.ToString(), cookie.Value)
            {
                // __Host requires Path=/ and forbids Domain cookies planted by
                // sibling origins. ApplyRequest enforces the policy's path.
                Path = "/",
                Secure = true,
                HttpOnly = true,
                SameSite = (int)sameSite > (int)minimum ? sameSite : minimum,
                MaxAge = cookie.MaxAge,
                Expires = cookie.Expires,
            };
            // RFC 6265 gives Max-Age precedence over Expires.
            var deleting = forwarded.MaxAge is { } age
                ? age <= TimeSpan.Zero
                : forwarded.Expires <= now;
            if (deleting)
            {
                forwarded.MaxAge = TimeSpan.Zero;
                forwarded.Expires = DateTimeOffset.UnixEpoch;
            }
            else if (policy.MaxAge is { } limit)
            {
                if (forwarded.MaxAge > limit)
                    forwarded.MaxAge = limit;
                if (forwarded.Expires > now + limit)
                    forwarded.Expires = now + limit;
            }
            // Preserve browser restrictions not modeled as typed properties, such
            // as Partitioned, rather than silently making the cookie unpartitioned.
            foreach (var extension in cookie.Extensions)
                forwarded.Extensions.Add(extension);
            context.HttpContext.Response.Headers.Append(
                HeaderNames.SetCookie,
                forwarded.ToString()
            );
        }
        return true;
    }

    internal static bool ApplyApplicationRequest(
        RequestTransformContext context,
        BffClient client,
        string clusterId,
        bool allowForeignOriginCookies
    )
    {
        var origin = context.HttpContext.Request.Headers.Origin;
        var fetchSite = context.HttpContext.Request.Headers["Sec-Fetch-Site"];
        if (
            !allowForeignOriginCookies
            && (
                origin.Count > 0
                    && (
                        origin.Count != 1
                        || origin[0]
                            != client.Options.PublicOrigin!.GetLeftPart(UriPartial.Authority)
                    )
                || fetchSite.Count > 0
                    && (fetchSite.Count != 1 || fetchSite[0] is not ("same-origin" or "none"))
            )
        )
            return true;
        var raw = context.HttpContext.Request.Headers.Cookie;
        if (raw.Count == 0)
            return true;
        if (
            !CookieHeaderValue.TryParseStrictList(
                raw.Select(value => value!).ToArray(),
                out var cookies
            )
        )
            return false;
        var prefix = ApplicationPrefix(client, clusterId);
        var requestPath =
            context.HttpContext.Request.PathBase.ToUriComponent()
            + context.HttpContext.Request.Path.ToUriComponent();
        var selected = new List<CookieHeaderValue>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cookie in cookies)
        {
            var name = cookie.Name.ToString();
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var scoped = name[prefix.Length..];
            var separator = scoped.IndexOf('~');
            if (separator <= 0)
                return false;
            string path;
            try
            {
                path = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(scoped[..separator]));
            }
            catch (FormatException)
            {
                return false;
            }
            if (
                !IsCanonicalPath(client, path)
                || WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(path)) != scoped[..separator]
            )
                return false;
            // Browser cookies must be rooted for __Host. Their original path
            // remains an exact, case-sensitive forwarding boundary on the server.
            if (!PathMatches(requestPath, path))
                continue;
            var original = scoped[(separator + 1)..];
            if (
                !CookieHeaderValue.TryParse(original + "=value", out var parsed)
                || parsed.Name.ToString() != original
                || !names.Add(original)
            )
                return false;
            selected.Add(new CookieHeaderValue(original, cookie.Value));
        }
        if (selected.Count > 0)
        {
            context.ProxyRequest.Headers.TryAddWithoutValidation(
                HeaderNames.Cookie,
                string.Join("; ", selected)
            );
            context.HttpContext.Response.Headers.CacheControl = "no-store";
        }
        return true;
    }

    internal static bool ApplyApplicationResponse(
        ResponseTransformContext context,
        BffClient client,
        string clusterId,
        DateTimeOffset now
    )
    {
        if (context.ProxyResponse?.Headers.TryGetValues(HeaderNames.SetCookie, out var raw) != true)
            return true;
        if (!SetCookieHeaderValue.TryParseStrictList(raw!.ToArray(), out var cookies))
            return false;
        var prefix = ApplicationPrefix(client, clusterId);
        var names = new HashSet<string>(StringComparer.Ordinal);
        // RFC 6265 default path when the backend omits Path.
        var requestPath = context.HttpContext.Request.Path.ToUriComponent();
        var slash = requestPath.LastIndexOf('/');
        var defaultPath = slash <= 0 ? "/" : requestPath[..slash];
        var forwarded = new List<string>();
        foreach (var cookie in cookies)
        {
            var path = cookie.Path.HasValue ? cookie.Path.ToString() : defaultPath;
            if (!IsCanonicalPath(client, path) || !names.Add(cookie.Name.ToString()))
                return false;
            var pathBase = client.Options.PathBase;
            var alreadyScoped =
                cookie.Path.HasValue
                && pathBase.Length > 0
                && (path == pathBase || path.StartsWith(pathBase + "/", StringComparison.Ordinal));
            var publicPath = alreadyScoped ? path : pathBase + path;
            var scope = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(publicPath));
            var rewritten = new SetCookieHeaderValue(
                prefix + scope + "~" + cookie.Name.ToString(),
                cookie.Value
            )
            {
                Path = "/",
                Secure = true,
                HttpOnly = true,
                SameSite =
                    cookie.SameSite == HeaderSameSite.Unspecified
                        ? HeaderSameSite.Lax
                        : cookie.SameSite,
                MaxAge = cookie.MaxAge,
                Expires = cookie.Expires,
            };
            var deleting = rewritten.MaxAge is { } age
                ? age <= TimeSpan.Zero
                : rewritten.Expires <= now;
            if (deleting)
            {
                rewritten.MaxAge = TimeSpan.Zero;
                rewritten.Expires = DateTimeOffset.UnixEpoch;
            }
            foreach (var extension in cookie.Extensions)
                rewritten.Extensions.Add(extension);
            forwarded.Add(rewritten.ToString());
        }
        foreach (var cookie in forwarded)
            context.HttpContext.Response.Headers.Append(HeaderNames.SetCookie, cookie);
        if (forwarded.Count > 0)
            context.HttpContext.Response.Headers.CacheControl = "no-store";
        return true;
    }

    private static string ApplicationPrefix(BffClient client, string clusterId) =>
        $"__Host-Http-oidcproxy-app~{client.Name}~{WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(clusterId))}~";

    private static string Prefix(BffClient client, string policyName) =>
        $"__Host-Http-oidcproxy~{client.Name}~{policyName}~";

    private static bool IsCanonicalPath(BffClient client, string path) =>
        path.StartsWith('/')
        && !path.StartsWith("//", StringComparison.Ordinal)
        && Uri.TryCreate(client.Options.PublicOrigin, path, out var target)
        && target.AbsolutePath == path
        && target.Query.Length == 0
        && target.Fragment.Length == 0;

    private static bool PathMatches(string requestPath, string cookiePath) =>
        requestPath.StartsWith(cookiePath, StringComparison.Ordinal)
        && (
            requestPath.Length == cookiePath.Length
            || cookiePath.EndsWith('/')
            || requestPath[cookiePath.Length] == '/'
        );
}
