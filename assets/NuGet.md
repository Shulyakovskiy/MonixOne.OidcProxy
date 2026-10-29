# MonixOne.OidcProxy.Yarp

Version 7.0.0 requires `Bff:EncryptionKey` and uses a new JWE session format.
When upgrading from 6.x, stop old replicas, select a new `Bff:KeyNamespace` and have users sign in again.

A .NET 10 LTS Backend-for-Frontend for single-page applications, built on ASP.NET Core OpenID Connect, YARP and Redis.

- Multiple SPA origins in one YARP host, with named OIDC/cookie schemes and isolated per-client Redis sessions.
- Confidential Authorization Code + PKCE with standard OIDC discovery and RP-initiated logout.
- OAuth tokens stay in server-side Redis sessions encrypted directly with `jose-jwt`. The browser receives a Secure, HttpOnly, host-only session cookie.
- Atomic cross-replica refresh coordination and logout invalidation; no retry of an ambiguous rotating refresh-token exchange.
- Explicit per-route application cookie policies, with isolated browser namespaces and original backend names for OAuth correlation callbacks.
- Same-origin SPA endpoints with CSRF protection, server access-token forwarding to configured HTTPS YARP destinations, and no in-memory session fallback.
- Interoperability with OpenIddict, including encrypted access tokens, refresh, revocation and logout.

Register a host-owned StackExchange.Redis `ConnectionMultiplexer` and standard YARP configuration, then call `AddOidcProxy(configuration.GetRequiredSection("Bff"))` and `UseOidcProxy`.
Client registration is automatic from the Clients collection in that section.

The public namespace is `MonixOne.OidcProxy.Yarp`; OIDC configuration uses the standard `OpenIdConnectOptions`.

[Setup and architecture](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp#readme) · [React/Vite sample and interactive SPA documentation](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/tree/main/samples/MonixOne.OidcProxy.Yarp.SampleHost) · [Verification](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/blob/main/docs/TESTING.md)

Set the required `Bff__EncryptionKey` through a secret configuration provider: a strong random PBES2 password.
The package uses standard `jose-jwt` `JWT.Encode` / `JWT.Decrypt` with PBES2-HS256+A128KW and A256GCM
to encrypt session JSON directly. Data Protection is used only for framework cookies, OIDC state and antiforgery;
its shared key ring is also stored in Redis and protected with the configuration secret. No shared directory is needed.
Generate the secret once, for example with `openssl rand -base64 32`, and keep it stable across restarts and replicas.
When upgrading from the previous session format or replacing the key, stop all replicas, select a new `Bff:KeyNamespace`,
and remove the old sessions/key ring. Users must sign in again.
Infrastructure must provide Redis availability/TLS/ACL and persistent, separate storage for the configuration key.
Local logout cannot cancel an already-forwarded API request or guarantee global revocation during an identity-provider outage.

See the repository for security boundaries and the upstream advisory status.

Based on OidcProxy.Net.
Licensed under LGPL-3.0-only; upstream copyright retained.
