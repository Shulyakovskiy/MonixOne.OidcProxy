# Migration from OidcProxy.Net 5.x

Version 6 is a deliberate security/API migration and requires .NET 10 LTS. Package IDs, assembly names and namespaces are `MonixOne.OidcProxy`, `.OpenIdConnect`, `.Auth0` and `.EntraId`. It does not read or migrate v5 browser sessions or unprefixed Redis keys; users sign in again.

`AddOidcProxy(config, options => ...)` and `UseOidcProxy()` remain the primary integration shape. The public origin is now mandatory and HTTPS only. Configure a confidential authorization-code client with PKCE, offline_access, exact callback and logout redirect URIs. Preserve separate clients/scopes/tenants for independent applications. `web-app`/`lkb-web` public clients must not be given a server secret in the SPA; register separate confidential BFF clients.

Redis is a host-owned ConnectionMultiplexer supplied through `ConfigureRedisBackBone(connection, projectName, environmentName, keyNamespace)`. All three scope segments participate in both session keys and Data Protection isolation. Never share a scope between different clients or environments.

The manual IIdentityProvider, JWT parser/signature-validator, encryption-key, redirect-factory and authentication-callback abstractions have been removed. Protocol validation now belongs to ASP.NET Core and Microsoft.IdentityModel. Use standard OpenIdConnectOptions/OnTokenValidated for application claim customization; session creation and security invariants belong to this library. Do not weaken signature, issuer, audience, nonce or PKCE requirements in custom application code.

The v5 "always valid" Entra validator is removed. The Entra convenience wrapper requires a concrete tenant GUID. Multi-tenant issuer policy and sovereign-cloud authority configuration should use the generic OIDC integration with an explicit verified authority; they are not silently inferred. Auth0 uses its standard discovered OIDC logout endpoint; legacy /v2/logout/federated flags are removed.

The /me response contains arrays of claim values, excludes the internal session reference, and never includes tokens. Every API call requires X-CSRF: 1. Logout is POST-only. Proxy APIs require authentication by default. Locally hosted APIs must opt into `.AsOidcProxyApi()`.

The implementation-coupled v5 session mocks/browser fixtures have been replaced with independent HTTP requests, real protocol state/cookies, signed test ID tokens, one-use code/refresh grants and real Redis tests. The upstream malicious-redirect regression corpus is retained. The nonfunctional v5 bundled demos and prebuilt Angular artifacts have been removed; their original source remains available in the upstream repository/history. The current compilable host is in samples/MonixOneBff.Host.

This is not a drop-in middleware upgrade. Test cookie domain/path/HTTPS, callback paths, reverse-proxy routing, scopes, tenant boundaries, login UI and logout with the actual identity provider before deployment. A standard BFF expects an interactive Identity login when /connect/authorize has no active session; BR.Identity's existing 401-only challenge must be integrated with a login UI/application flow.
