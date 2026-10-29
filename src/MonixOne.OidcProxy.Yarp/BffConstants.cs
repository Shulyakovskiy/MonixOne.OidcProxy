namespace MonixOne.OidcProxy.Yarp;

internal static class BffConstants
{
    internal const string PreviousSession = ".oidcproxy.previous-session";
    internal const string OriginalNonce = ".oidcproxy.original-nonce";
    internal const string OriginalAuthTime = ".oidcproxy.original-auth-time";
    internal const string AuthorizationPolicy = "MonixOne.OidcProxy.Yarp.Session";
    internal const string SessionClaim = "oidcproxy:session";
    internal static readonly object ApiMetadata = new();
    internal static readonly object SignInRefreshTokenItem = new();
    internal static readonly object TicketItem = new();
    internal static readonly object LogoutIdTokenItem = new();
    internal static readonly object RemovedSessionItem = new();
    internal static readonly object SessionSnapshotItem = new();
}
