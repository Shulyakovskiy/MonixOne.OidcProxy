# Verification matrix

The suite runs on .NET 10 LTS. Docker is required; Redis tests use an isolated `redis:8-alpine` Testcontainers instance. The test Redis never uses an application's production or development data.

## OIDC and browser sessions

- Complete authorization-code flow with S256 PKCE, signed ID tokens, browser state/correlation and nonce cookies.
- Wrong issuer, audience, nonce, signature, expired/unsigned tokens, missing subject/iat and invalid authorized party.
- Malformed token responses, duplicate callbacks, independent login tabs, session-reference rotation and old-cookie invalidation.
- A principal from another host middleware/authentication scheme cannot bypass the required BFF cookie session.
- No OAuth tokens in the cookie or /me response. Only the server-side access token reaches downstream; incoming bearer headers cannot replace it.
- CSRF headers, Origin and Fetch Metadata checks; downstream Cookie/Set-Cookie stripping; private no-store responses.
- POST-only logout, native form logout with ASP.NET Core antiforgery, forged/cross-origin form rejection and remote revocation.
- Signing-key rollover through the maintained configuration manager.

## Concurrent refresh and failure behavior

- Concurrent independent HTTP requests and /me requests share one refresh result.
- Two separate BFF hosts and two ConnectionMultiplexer instances against real Redis.
- Different sessions refresh independently; cancelled waiters do not cancel the owner; caller disconnection does not lose a rotated token.
- Logout on another replica wins against an in-flight refresh; rejected late credentials are revoked.
- A pending marker left by a crashed worker cannot be redeemed again. Deadline expiry is fail-closed.
- invalid_grant, identity-server errors, transport ambiguity, missing access token, invalid token type/expiry and invalid refreshed ID tokens.
- Optional omitted refresh/ID tokens preserve the previous credentials where permitted.
- Idle and absolute expiration, no extension of absolute lifetime, access-token-only sessions, stale CAS writers and non-resurrection after logout.
- Redis outage returns 503; an unpersisted rotation cannot reuse the old refresh token after recovery. A new host reads the shared protected session after restart.
- Protected Redis values and TTL; project/environment/namespace isolation of sessions and Data Protection keys; rejection of namespace escape characters.

The malicious local-redirect regression corpus from upstream is retained. Wrapper tests verify Auth0 and tenant-specific Entra configuration; live external tenants and a specific BR.Identity deployment are not simulated as successful integrations.

Run all tests with `dotnet test MonixOne.OidcProxy.sln -c Release`. Repeat concurrency tests with:

```sh
dotnet test tests/MonixOne.OidcProxy.Tests/MonixOne.OidcProxy.Tests.csproj -c Release --filter 'FullyQualifiedName~SessionTests|FullyQualifiedName~RedisConcurrencyTests|FullyQualifiedName~IndependentHttpRequests'
```

A finite suite is not proof that every possible deployment/failure is covered. In particular, asynchronous Redis failover can lose acknowledged state; the identity provider and Redis do not participate in a distributed transaction. See README for the explicit guarantees and deployment limits.

## React/Vite browser client

ClientApp has focused Vitest/Testing Library tests for the BFF request contract, same-origin credentials and CSRF headers, 401 vs 503 handling, cancellation forwarding, malformed response contracts, protected API rendering, and native antiforgery POST logout. No token library, bearer header, localStorage or sessionStorage is used. TypeScript checking and Vite production build are separate required checks. See the sample README for local HTTPS setup.

## Verified snapshot — 2026-10-28

- .NET SDK 10.0.403 / runtime 10.0.14: Release build passed with zero warnings/errors; 372 tests passed, zero skipped.
- A separate repeat of the session/concurrency/real-Redis tests passed: 42 tests, zero skipped.
- React sample: 12 tests passed; TypeScript checking and Vite production build passed.
- All four 6.0.0 NuGet packages were built and their package IDs, net10.0 assemblies and LGPL-3.0-only metadata inspected. Publishing the host included the compiled SPA.
- NuGet vulnerability audit including transitive dependencies and npm audit reported no known vulnerabilities at the time of this run.

These are local verification results. A live identity-provider deployment, Redis failover topology and external CI must be checked separately.
