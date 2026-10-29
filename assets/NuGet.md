# MonixOne.OidcProxy.Yarp

A .NET 10 BFF for SPAs using ASP.NET Core OpenID Connect, YARP and Redis.
OAuth tokens stay in encrypted server sessions; the browser receives a Secure, HttpOnly cookie.

```csharp
builder.Services.AddOidcProxy(builder.Configuration);
// After builder.Build():
app.UseOidcProxy();
```

The package configures YARP, Redis, trusted forwarded headers and all named OIDC clients.
`Bff:Clients:<client>:Routes` owns BFF route bindings, `AllowAnonymous` and optional `CookiePolicy`.
`ReverseProxy` retains native YARP hosts, paths, transforms and HTTP/HTTPS destinations.
TLS may terminate at the load balancer with HTTP on the trusted internal network.
Public origins, OIDC Authority and callback URLs remain HTTPS.

Version 8.0.0 changes the configuration API: pass the root configuration instead of the Bff section,
and move previous OidcProxy.Client / OidcProxy.CookiePolicy route metadata into Bff client Routes.
The encrypted session format remains compatible with 7.0.0.

Set `ConnectionStrings__Redis`, `Bff__EncryptionKey` and each confidential client secret through a secret provider.
Keep the encryption key and Redis scope consistent across restarts and replicas.
Configure the actual trusted load balancer IP/CIDR; internal HTTP carries credentials without encryption.

[Setup and migration](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp#readme) · [Sessions and Redis](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/blob/main/docs/SESSIONS.md) · [Sample](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/tree/main/samples/MonixOne.OidcProxy.Yarp.SampleHost)

Based on [OidcProxy.Net](https://github.com/oidcproxydotnet/OidcProxy.Net).
Licensed under LGPL-3.0-only; upstream copyright retained.
