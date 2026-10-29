using Microsoft.AspNetCore.Http;
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
        var path = client.Options.PathBase + policy.Path;
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
                // Host-only, scoped and always inaccessible to JavaScript. Native
                // prefixes from the backend are restored only on the bound cluster.
                Path = path,
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

    private static string Prefix(BffClient client, string policyName) =>
        $"__Secure-oidcproxy~{client.Name}~{policyName}~";
}
