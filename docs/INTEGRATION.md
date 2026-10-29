# YARP and BR.Identity integration

## Placement

Place the package in a browser BFF host containing YARP, served from the SPA's HTTPS origin. BR.Identity remains the external OIDC authorization server. YARP forwards the server-held access token to existing APIs; those APIs continue validating issuer, audience, scope, tenant and roles. These are complementary responsibilities.

A dedicated browser host is the simplest deployment. A shared YARP gateway can keep its Bearer/M2M routes using the explicit selection below. Embedding the BFF in the Identity process combines the authorization server and client session boundary; it is not the recommended integration. The package configures host Data Protection and middleware. This verification covers OIDC interoperability, not unchanged in-process ASP.NET Identity cookies.

## Shared gateway

Register existing authentication and YARP **before** AddOidcProxy. The package reuses registered YARP services and providers, preserving authentication defaults and authorization DefaultPolicy/FallbackPolicy. BFF APIs use a separate policy after explicitly authenticating the BFF cookie. A host Bearer principal cannot replace it. Named BFF policies should evaluate that refreshed principal without selecting another authentication scheme.

```csharp
// Keep the host's existing AddAuthentication/AddJwtBearer and authorization setup.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetRequiredSection("ReverseProxy"));
var config = builder.Configuration.GetRequiredSection("OidcProxy")
    .Get<OidcProxyConfig>(binder => binder.ErrorOnUnknownConfiguration = true)!;
config.ProtectOnlyMarkedRoutes = true;
builder.Services.AddOidcProxy(config, options =>
    options.ConfigureRedisBackBone(redis, "BeetRoute",
        builder.Environment.EnvironmentName, "admin-bff"));

// After trusted forwarded-header handling and any public static-file middleware:
app.UseOidcProxy();
```

UseOidcProxy registers routing, authentication, BFF checks and authorization, and maps YARP once. Do not also call MapReverseProxy. Omit OidcProxy:ReverseProxy when loading the same routes from the host's ReverseProxy section. Existing rate-limit/timeout/cache middleware remains the host's responsibility. Route conversion preserves the complete YARP record, including these policies.

Do not enable output caching for authenticated BFF API routes. Their responses are private/no-store; custom gateway cache policies must respect this boundary.

```json
{
  "OidcProxy": {
    "ProtectOnlyMarkedRoutes": true,
    "CustomHostName": "https://admin.example.com",
    "Oidc": {
      "Authority": "https://identity.example.com",
      "ClientId": "admin-bff",
      "Scopes": ["profile-service.full_access"]
    }
  },
  "ReverseProxy": {
    "Routes": {
      "browser-profile": {
        "ClusterId": "profile",
        "Match": { "Path": "/bff/profile/{**all}" },
        "Metadata": { "MonixOne.OidcProxy.Bff": "true" },
        "Transforms": [{ "PathRemovePrefix": "/bff/profile" }]
      },
      "machine-profile": {
        "ClusterId": "profile",
        "AuthorizationPolicy": "existing-machine-policy",
        "Match": { "Path": "/api/profile/{**all}" }
      }
    },
    "Clusters": {
      "profile": { "Destinations": { "primary": { "Address": "https://profile.internal/" } } }
    }
  }
}
```

Policy/destination names are examples. Supply the client secret and Redis connection externally. Route metadata accepts true/false; an invalid value rejects route configuration. Default ProtectOnlyMarkedRoutes=false keeps the dedicated BFF behavior protecting every proxy route.

- Marked routes require the BFF cookie, X-CSRF: 1 and same-origin checks. Incoming Cookie/Authorization are removed, the stored access token is supplied, and downstream Set-Cookie is removed.
- Unmarked routes retain existing authorization policies, Bearer headers, ordinary cookies and downstream cookies without BFF CSRF requirements. BFF session/antiforgery cookies and session chunks are still stripped. Downstream cannot set those reserved cookie names through an unmarked route.
- Login is explicitly anonymous even with a host FallbackPolicy. /me, /antiforgery, logout and local AsOidcProxyApi endpoints always require the BFF session.

## BR.Identity compatibility

Tests use real OpenIddict **7.7.2**, as currently referenced by the inspected BR.Identity source, with disposable PostgreSQL. They exercise discovery/JWKS, code + PKCE, signed ID tokens, rotating refresh, revocation and logout through ASP.NET Core TestServer. The fixture reproduces the client_id query/form tenant-selection boundary and claims/resource contract. It is not a deployment of Identity's business handlers.

| Contract | Package behavior |
| --- | --- |
| /connect/authorize, /connect/token, /connect/revocation, /connect/logout, /.well-known/jwks | Discovered from metadata; runtime code does not hardcode provider paths. |
| Tenant selection reads client_id from query/form before OpenIddict | Refresh/revocation use client_secret_post, matching code exchange. Logout includes client_id without requiring an Identity cookie. |
| ID token audience is the confidential client ID | Framework validation uses that client ID, not beetroute-api. |
| Access token typ=at+jwt, aud=beetroute-api, tenant_id and service scopes | Original access token is forwarded unchanged. Browser-supplied X-Tenant-Id is never promoted to trusted tenant identity. |
| Current scope profile-service.full_access | Sample uses this value. The obsolete profile-service:fullaccess is rejected by the real server test. |
| Roles are emitted only in access tokens | /me does not invent roles or decode an access token into its principal. Resource servers continue enforcing authorization. |
| Refresh rotates credentials within an absolute lifetime | A single pending CAS operation redeems the token; the complete result is persisted together. |

## Preparation outside this package

Identity currently seeds web-app/lkb-web as public SPA clients. A BFF needs a **separate confidential client**, secret, code/refresh and selected scope permissions, and exact callback/logout URIs. Register its client ID in the appropriate Identity tenant's client catalogue as well as OpenIddict. A database application entry alone cannot satisfy tenant selection. Do not convert existing public/mobile/M2M clients into the BFF client.

The current Identity authorization handler returns 401 without an Identity browser session; its session handler does not redirect to a login UI. A first-time browser login still needs login/authorization continuation outside this package. Tests preserve that 401 instead of claiming a successful anonymous flow. Existing authenticated sessions work with the tested protocol.

No service repository is changed. No live BR.Identity deployment or complete BR.GW checkout was available. OTP/passkey UX, seeding, consent, profile exchange, infrastructure TLS and actual routing remain deployment-specific checks.

References: [YARP authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/authn-authz?view=aspnetcore-10.0), [OpenIddict ASP.NET Core integration](https://documentation.openiddict.com/integrations/aspnet-core).
