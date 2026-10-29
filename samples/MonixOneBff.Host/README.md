# React + Vite BFF sample

The frontend lives in ClientApp and uses React, Vite and TypeScript. Angular and its prebuilt bundles are removed. React calls the same-origin .NET BFF with HttpOnly cookies and X-CSRF: 1; it never stores OAuth tokens or attaches a bearer header.

## Production-style local run

1. Supply ConnectionStrings__Redis and OidcProxy__Oidc__ClientSecret through the host configuration/environment. Configure a real confidential OIDC client, exact callback/logout URIs and API scopes in appsettings/environment. Keep separate Redis project/environment/namespace scopes for each app and environment.
2. Build the frontend before publishing the host:

```sh
cd samples/MonixOneBff.Host/ClientApp
npm ci
npm test
npm run build
cd ..
dotnet publish -c Release -o ../../../artifacts/sample-host
```

Vite writes compiled assets to the host's wwwroot, which the Web SDK publishes. Generated assets and node_modules are ignored by Git. The sample includes a real local protected endpoint /demo-api/status to exercise the cookie/CSRF boundary without assuming an external API route. Its configured /api/* routes are proxied to the gateway.

Set the host's public HTTPS origin to the actual browser origin. With the supplied https://localhost:5001 example, the exact login callback is https://localhost:5001/.auth/login/callback and the signed-out callback is https://localhost:5001/.auth/signout-callback. The host needs a trusted HTTPS certificate, or TLS must terminate at a correctly configured reverse proxy. Secrets are never passed through VITE_* variables.

## Vite development

Use Node.js 24.15+ (24 LTS is used in CI). Start the .NET host on HTTPS port 5001, and configure OidcProxy__CustomHostName=https://localhost:5173 for this development run. Register the corresponding exact callback and signed-out callback on the development Identity client.

Provide a trusted local PEM certificate/key to DEV_HTTPS_CERT and DEV_HTTPS_KEY. If Node does not trust the backend's local CA, supply its CA certificate through NODE_EXTRA_CA_CERTS. Then run npm run dev from ClientApp. BFF_TARGET defaults to https://localhost:5001. Certificate validation remains enabled; there is no insecure HTTP or certificate-bypass fallback.

Vite proxies /.auth, /api and /demo-api to .NET so the browser sees one HTTPS origin and HMR works. No Node BFF or client-side OAuth library is involved. No certificate is needed for npm test or npm run build.

## Logout navigation

The client obtains an ASP.NET Core antiforgery request token from GET /.auth/antiforgery, then submits a native POST form to /.auth/end-session. The browser follows the OIDC redirect as a top-level navigation. It does not fetch a cross-origin logout URL, and no ID/access/refresh token is exposed to React.

The UI distinguishes anonymous sessions (401), forbidden requests (403), unavailable backend/Identity (503), cancellation and successful authenticated API responses. The frontend tests cover these transitions and the native form contract; the .NET suite verifies the actual antiforgery cookie/form validation and OIDC redirect.
