# MonixOne.OidcProxy.Yarp

A .NET 10 BFF for SPAs using ASP.NET Core OpenID Connect, YARP and Redis.
OAuth tokens remain in encrypted server sessions; the browser receives a Secure, HttpOnly cookie.
Native YARP routes, JWT policies and load balancing remain active, including without SPA profiles.

Login uses a session cookie and the existing idle/absolute limits by default.
To remember a browser, explicitly pass `rememberMe=true` to `/.auth/login` with
the local `returnUrl`. Framework-protected OIDC state carries the choice through
the callback. The persistent cookie and encrypted Redis session share a fixed
30-day lifetime without a shorter inactivity timeout. Refresh and activity never
extend that deadline. Configure `Bff:Clients:<client>:RememberedSessionTimeout`
to change it; logout, revocation and refresh-token expiry can end a session earlier.

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

## Forwarded headers behind a private ingress

The package configures and invokes ASP.NET Core forwarded headers middleware once,
before routing and the host callback containing observability and rate limiting.
It restores `HttpContext.Connection.RemoteIpAddress`, `Request.Scheme` and
`Request.Host`. Do not add host `Configure/PostConfigure<ForwardedHeadersOptions>`
or another `UseForwardedHeaders()` call. A separate Identity provider owns its own
HTTP pipeline and does not need this BFF package just to process forwarded headers.

For a private gateway behind a controlled edge proxy or routing mesh, add this
fragment to the configuration of the intended environment:

```json
{
  "ForwardedHeaders": {
    "TrustAllProxies": true,
    "AllowedHosts": ["gw.example.com", "spa.example.com"]
  }
}
```

This opt-in clears `KnownProxies` and `KnownIPNetworks`, retaining the framework's default `ForwardLimit=1`.
The rightmost `X-Forwarded-For` value becomes `RemoteIpAddress`; application code
should use that property instead of parsing the first header value. The edge must
replace untrusted forwarded headers or append its observed peer IP last, and set
the public scheme and host. The resulting IP is the address asserted by the edge;
if another load balancer precedes it, establish that trust at the edge first.

Prevent direct untrusted access to the gateway: a direct sender can otherwise
spoof forwarded IP, scheme and host (within `AllowedHosts`). No Swarm IP/subnet
list or changes to ingress publication, replica count or `start-first` are needed.
The flag is not tied to an environment name. When absent or false, strict
`KnownProxies` / `KnownIPNetworks` checks remain the default. `AllowedHosts`, BFF
origin checks, CORS, CSRF and authentication remain enforced in either mode.

See [ASP.NET Core proxy configuration](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

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
