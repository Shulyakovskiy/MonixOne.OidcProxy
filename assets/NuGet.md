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

Configure confidential clients, exact HTTPS callbacks and a trusted proxy boundary.
Supply Redis, `Bff__EncryptionKey` and client secrets through a secret provider.
Keep the Redis scope and encryption key consistent across replicas and restarts.

[Interactive setup guide](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp#readme):
configuration generator, middleware/flow diagrams, route simulator, sessions, migration and testing.
Download `docs/index.html` from the repository to use the guide offline as a single file.

Based on [OidcProxy.Net](https://github.com/oidcproxydotnet/OidcProxy.Net).
Licensed under LGPL-3.0-only; upstream copyright retained.
