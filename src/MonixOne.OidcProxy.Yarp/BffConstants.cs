namespace MonixOne.OidcProxy.Yarp;

internal static class BffConstants
{
    /// <summary>
    /// Stable Data Protection purpose isolating server-session payloads from other artifacts.
    /// The client Redis scope is appended as a separate purpose segment.
    /// </summary>
    internal const string SessionProtectionPurpose = "MonixOne.OidcProxy.Yarp.ServerSession";

    internal const string PreviousSession = ".oidcproxy.previous-session";
    internal const string OriginalNonce = ".oidcproxy.original-nonce";
    internal const string OriginalAuthTime = ".oidcproxy.original-auth-time";
    internal const string ProviderIssuer = ".oidcproxy.provider-issuer";
    internal const string ProviderSubject = ".oidcproxy.provider-subject";
    internal const string ProviderSession = ".oidcproxy.provider-session";
    internal const string AuthorizationPolicy = "MonixOne.OidcProxy.Yarp.Session";
    internal const string SessionClaim = "oidcproxy:session";
    internal static readonly object ApiMetadata = new();
    internal static readonly object SignInRefreshTokenItem = new();
    internal static readonly object TicketItem = new();
    internal static readonly object LogoutIdTokenItem = new();
    internal static readonly object RemovedSessionItem = new();
    internal static readonly object SessionSnapshotItem = new();
}
