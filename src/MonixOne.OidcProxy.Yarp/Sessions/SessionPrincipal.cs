using System.Security.Claims;

namespace MonixOne.OidcProxy.Yarp.Sessions;

internal static class SessionPrincipal
{
    internal static ClaimsPrincipal WithSessionId(ClaimsPrincipal principal, string id)
    {
        var clone = principal.Clone();
        foreach (var identity in clone.Identities)
        foreach (var claim in identity.FindAll(BffConstants.SessionClaim).ToArray())
            identity.RemoveClaim(claim);
        ((ClaimsIdentity)clone.Identity!).AddClaim(new Claim(BffConstants.SessionClaim, id));
        return clone;
    }
}
