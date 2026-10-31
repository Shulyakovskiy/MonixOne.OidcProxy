# MonixOne.OidcProxy.Yarp

A .NET 10 BFF for SPAs using ASP.NET Core OpenID Connect, YARP and Redis.
OAuth tokens remain in encrypted server sessions; the browser receives a Secure, HttpOnly cookie.
Native YARP routes, JWT policies and load balancing remain active, including without SPA profiles.

```csharp
builder.Services.AddOidcProxy(builder.Configuration);
// After builder.Build():
app.UseOidcProxy();
```

Pass the root configuration. Use the `UseOidcProxy(pipeline => …)` callback for host
observability, CORS and rate limiting. The package owns YARP registration, trusted
forwarded headers and shared Redis registration. Exact native YARP hosts select SPA
clients; `ForwardCookies` enables isolated backend cookies without provider-specific lists.
The cookie bridge uses `__Host-Http-` cookies with `Path=/` and no Domain, preventing
sibling-domain cookie tossing. Original backend paths are enforced during forwarding.

Configure confidential clients, exact HTTPS callbacks and a trusted proxy boundary.
Supply Redis and client secrets through a secret provider. Protect the shared key
ring with `Bff__EncryptionKey` or the host's standard certificate configuration.
Keep the Redis scope and key-ring decryption configuration consistent across replicas and restarts.

ASP.NET Core Data Protection encrypts sessions and manages data-key rotation in the
shared Redis key ring. Hosts can use standard certificate key protection instead
of the password wrapper. This format targets a new deployment; no migration reader is included.
Signed OIDC back-channel logout is available at `/.auth/backchannel-logout`.
Configure its limits through each client's `BackchannelLogout` options.
Use `/.auth/me?slide=false` to inspect a session without renewing idle expiry.
`ClientCredentialStyle` selects POST-body or Basic authentication consistently for
PAR, code exchange, refresh and revocation. OIDC `Events` callbacks are composed;
`EventsType` is not supported. Collect the `MonixOne.OidcProxy.Yarp` Meter and
ActivitySource with the host's telemetry stack.

[Interactive setup guide](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp#readme):
configuration generator, middleware/flow diagrams, route simulator, sessions and testing.
Download `docs/index.html` from the repository to use the guide offline as a single file.

Based on [OidcProxy.Net](https://github.com/oidcproxydotnet/OidcProxy.Net).
Licensed under LGPL-3.0-only; upstream copyright retained.
