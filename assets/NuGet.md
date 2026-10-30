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
Old `__Secure-` bridge cookies are ignored; restart pending OAuth connections after upgrading all replicas.

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

This opt-in clears `KnownProxies` and `KnownIPNetworks` and sets `ForwardLimit=1`.
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
Supply Redis, `Bff__EncryptionKey` and client secrets through a secret provider.
Keep the Redis scope and encryption key consistent across replicas and restarts.

[Interactive setup guide](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp#readme):
configuration generator, middleware/flow diagrams, route simulator, sessions, migration and testing.
Download `docs/index.html` from the repository to use the guide offline as a single file.

Based on [OidcProxy.Net](https://github.com/oidcproxydotnet/OidcProxy.Net).
Licensed under LGPL-3.0-only; upstream copyright retained.
