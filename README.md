# MonixOne.OidcProxy

A .NET 10 LTS backend for browser applications, forked from [OidcProxy.Net](https://github.com/oidcproxydotnet/OidcProxy.Net). Licensed under LGPL-3.0-only; the upstream copyright and license are retained.

The browser holds a framework-protected, Secure, HttpOnly, host-only cookie containing an opaque session reference. OIDC runs on the server using the ASP.NET Core OpenID Connect handler (Authorization Code + S256 PKCE, state, correlation and nonce). Access, refresh and ID tokens remain in a protected server-side record. YARP attaches the current bearer token to downstream requests and removes browser cookies and downstream Set-Cookie headers.

## Minimal host

```csharp
using MonixOne.OidcProxy.ModuleInitializers;
using MonixOne.OidcProxy.OpenIdConnect;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration.GetRequiredSection("OidcProxy")
    .Get<OidcProxyConfig>(o => o.ErrorOnUnknownConfiguration = true)!;
await using var redis = await ConnectionMultiplexer.ConnectAsync(
    builder.Configuration.GetConnectionString("Redis")!);

builder.Services.AddOidcProxy(config, options =>
    options.ConfigureRedisBackBone(redis,
        projectName: "BeetRoute",
        environmentName: builder.Environment.EnvironmentName,
        keyNamespace: "admin-identity"));

await using var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseOidcProxy();
await app.RunAsync();
```

The multiplexer belongs to the host. The library never creates a connection per request and never disposes the supplied multiplexer. All Redis keys, including the Data Protection key ring, are scoped by lowercase `project:environment:namespace:`. Segments allow ASCII letters, digits, dots, hyphens and underscores. Examples:

```text
beetroute:production:admin-identity:sessions:<opaque-id>
beetroute:production:admin-identity:data-protection
beetroute:development:admin-identity:sessions:<opaque-id>
```

Use a different namespace for each BFF/client or independently deployed application. Replicas of the same BFF must use the same scope and cookie settings. Use distinct CookieName values if multiple BFF applications share one browser origin.

See [the React + Vite host](samples/MonixOneBff.Host/README.md) and [migration notes](docs/MIGRATION-6.md) for complete configuration. Client secrets and Redis credentials come from the host's configuration provider or environment, never from browser code. The example expects `OidcProxy__Oidc__ClientSecret` and `ConnectionStrings__Redis` to be supplied externally.

## Browser contract

- Navigate to `GET /.auth/login` to start OIDC. Register the exact public HTTPS callback `/.auth/login/callback` on a **confidential** Identity client.
- Send `X-CSRF: 1` on every proxy/API call, including GET, and on `GET /.auth/me`. Cookies are sent to the BFF origin. No access or refresh token is returned to JavaScript.
- `POST /.auth/end-session` accepts the CSRF header or a validated ASP.NET Core antiforgery form token and ends the local server session before best-effort refresh-token revocation and OIDC logout. GET logout returns 405. For top-level browser navigation, fetch `GET /.auth/antiforgery` with X-CSRF: 1, then submit a native POST form using its requestToken/formFieldName; this follows the Identity logout redirect without cross-origin fetch.
- Mark locally hosted API endpoints with `.AsOidcProxyApi()` to apply authorization, refresh and CSRF checks. Proxy endpoints and `/me` receive these checks automatically.
- 401 means authentication must be restarted. 403 means the CSRF/origin check or authorization failed. 503 means session storage or the identity provider is unavailable; it is never converted to anonymous proxy access.

Prefer serving the SPA and BFF from one origin. The library does not enable cross-origin credential access. An Origin header must match the configured public origin; cross-origin Fetch Metadata is rejected. Static assets can be served separately before the BFF middleware.

## Refresh and failure guarantees

A compare-and-set transition records a unique pending refresh **before** calling the token endpoint. Competing requests wait for its result. The new access/refresh/ID token set is written in one protected record only if that pending operation still owns the session. Logout deletes the record, so a late refresh cannot recreate it. Late credentials are best-effort revoked.

No refresh or authorization-code redemption is automatically retried. A transport failure, worker crash or storage failure after token redemption can make the outcome unknown: the session then requires reauthentication. An expired pending operation is never reclaimed to redeem the old refresh token again. Availability is deliberately sacrificed rather than risking rotation replay or overwriting logout.

These guarantees rely on the configured Redis primary retaining acknowledged writes. Asynchronous Redis failover, restore from an old backup or split-brain can lose the pending marker. There is no distributed transaction or exactly-once guarantee spanning Redis and the identity provider. Configure Redis persistence, TLS, access controls and failover according to the application's requirements. A lost server session requires a new OIDC login.

Without Redis, storage is in-process with the same compare-and-set behavior; sessions do not survive a host restart and cannot be shared between replicas. A configured Redis outage never falls back to in-process storage.

## Security and deployment

- The public HTTPS origin must be explicitly configured with `CustomHostName` or `SetCustomHostName`. Redirect URLs do not trust request Host headers. Configure trusted forwarded headers and allowed hosts on the hosting application/reverse proxy.
- ID tokens must be signed and pass the framework's issuer, audience, lifetime, nonce and correlation validation. Unsigned ID tokens are rejected even in Code flow. Refreshed ID tokens must retain subject, issuer, audience, nonce (when returned), and authentication time. Access tokens can be opaque: the downstream API owns their validation.
- Session idle lifetime defaults to 20 minutes; absolute lifetime defaults to 8 hours. Refresh cannot extend the absolute lifetime. Tokens without a refresh token remain usable until actual access-token expiry.
- Server records use ASP.NET Core Data Protection. Its Redis key ring shares the application scope. To protect against full Redis read compromise, wrap Data Protection keys with an independently held certificate/key service using the host's standard `AddDataProtection().ProtectKeysWithCertificate(...)` configuration. Storing both unwrapped master keys and encrypted records in Redis is not protection against full Redis compromise.
- Do not log cookies, Authorization headers, token responses, callback query strings, client secrets or request bodies containing credentials. The sample suppresses Hosting.Diagnostics request URL logging; configure equivalent redaction on ingress, telemetry and logging sinks.
- Token revocation at logout is best effort and logged on failure. Already issued JWT access tokens may remain usable until expiry. Back-channel logout/session administration across the identity provider is not implemented by this library.

## Build and verification

Requires the .NET SDK pinned in `global.json` and Docker for the real Redis tests. The React/Vite sample uses Node.js 24.15+ and npm; run npm ci, npm test and npm run build in samples/MonixOneBff.Host/ClientApp before publishing the host.

```sh
dotnet restore MonixOne.OidcProxy.sln
dotnet build MonixOne.OidcProxy.sln -c Release --no-restore
dotnet test MonixOne.OidcProxy.sln -c Release --no-build
dotnet pack MonixOne.OidcProxy.sln -c Release --no-build -o artifacts/packages
```

Dependency versions are centralized in `Directory.Packages.props`; package references do not override versions. All runtime namespaces and assembly/package IDs are under `MonixOne.OidcProxy`.

The test matrix is documented in [docs/TESTING.md](docs/TESTING.md). Auth0 and Entra wrappers use the same validated framework handler; their production tenants and a particular BR.Identity deployment require deployment-specific integration testing. The fork does not claim that an undisclosed upstream CVE has been fixed without its technical details and affected versions.
