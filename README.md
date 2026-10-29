# MonixOne.OidcProxy.Yarp

BFF для SPA на .NET 10, ASP.NET Core OpenID Connect, YARP и Redis.
Браузер получает HttpOnly cookie; OAuth-токены хранятся на сервере.
Один хост может обслуживать несколько SPA с отдельными OIDC clients и сессиями.

```text
Браузер ──HTTPS──> балансировщик ──HTTP──> YARP / BFF ──HTTP──> приложение
```

## Пошаговая настройка

### 1. Зарегистрируйте OIDC client

У провайдера нужен confidential client с Authorization Code + PKCE, refresh grant и scopes вашего API.
Для origin `https://spa.example.com` зарегистрируйте:

- Redirect URI: `https://spa.example.com/.auth/login/callback`.
- Post logout redirect URI: `https://spa.example.com/.auth/signout-callback`.
- Scopes: `openid`, `offline_access` и необходимые API scopes.

Провайдер должен иметь browser login и поддерживать RP-initiated logout.
Для OpenIddict выдайте клиенту permissions для используемых endpoints, grants, response type `code` и API scopes.

### 2. Задайте конфигурацию

`Bff` содержит клиентов, сессии и привязки по RouteId.
`ReverseProxy` содержит обычную конфигурацию YARP: hosts, paths, transforms и destinations.

```json
{
  "AllowedHosts": "spa.example.com",
  "ForwardedHeaders": {
    "KnownProxies": ["10.0.0.10"],
    "AllowedHosts": ["spa.example.com"]
  },
  "Logging": {
    "LogLevel": {
      "Microsoft.AspNetCore.Hosting.Diagnostics": "Warning",
      "Microsoft.AspNetCore.Authentication.OpenIdConnect": "Warning",
      "Yarp.ReverseProxy.Forwarder.HttpForwarder": "Warning"
    }
  },
  "Bff": {
    "ApplicationName": "spa-gateway",
    "Clients": {
      "portal": {
        "PublicOrigin": "https://spa.example.com",
        "OpenIdConnect": {
          "Authority": "https://identity.example.com",
          "ClientId": "portal-bff",
          "Scope": ["openid", "offline_access", "api.read"]
        },
        "Routes": {
          "portal-api": { "AllowAnonymous": false }
        }
      }
    }
  },
  "ReverseProxy": {
    "Routes": {
      "portal-api": {
        "ClusterId": "portal-api",
        "Match": { "Hosts": ["spa.example.com"], "Path": "/api/{**path}" }
      }
    },
    "Clusters": {
      "portal-api": {
        "Destinations": {
          "primary": { "Address": "http://portal-api:8000/" }
        }
      }
    }
  }
}
```

Замените адреса и scopes своими. `KnownProxies` — IP непосредственного балансировщика,
который BFF видит как RemoteIpAddress; для доверенной подсети доступен `KnownIPNetworks` с CIDR.
Балансировщик сохраняет публичный Host и перезаписывает X-Forwarded-Proto/Host/For.
Публичные origin, Authority и callback URI остаются HTTPS. Внутренний HTTP не требует отдельных флагов,
но передаёт токены и данные без шифрования: доступ к нему ограничивается доверенной сетью.

Секреты передайте через environment или secret provider:

```text
ASPNETCORE_URLS=http://0.0.0.0:8000
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Redis=<Redis connection>
Bff__EncryptionKey=<отдельный случайный секрет>
Bff__Clients__portal__OpenIdConnect__ClientSecret=<OIDC client secret>
```

Сгенерируйте `EncryptionKey` один раз (`openssl rand -base64 32`) и сохраняйте между redeploy.
На репликах должны совпадать Redis, область ключей и секрет. Нужен Redis 6.2+.

### 3. Подключите пакет

Подключите `MonixOne.OidcProxy.Yarp` к хосту. Для локального пакета:

```sh
dotnet pack src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release -o artifacts/nuget
```

```csharp
using MonixOne.OidcProxy.Yarp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOidcProxy(builder.Configuration);

var app = builder.Build();
app.UseOidcProxy();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("{**path}", "index.html").AllowAnonymous();
app.Run();
```

Пакет сам читает конфигурацию, регистрирует YARP, Redis и всех клиентов,
применяет доверенные forwarded headers и подключает authentication/routing.
Дополнительные обходы Clients, регистрация схем и `MapReverseProxy()` в хосте не нужны.
Static files/fallback показаны для SPA в этом же процессе; отдельную SPA можно проксировать штатным YARP route.

### 4. Подключите SPA

```ts
window.location.assign("/.auth/login");

const response = await fetch("/.auth/me", {
  credentials: "same-origin",
  headers: { "X-CSRF": "1" },
});
```

SPA и BFF работают на одном публичном origin. `X-CSRF: 1` нужен и для GET к защищённым API.
При 401 очищайте данные пользователя и предлагайте вход; 403 означает отказ доступа, 503 — недоступность.
Logout: получить `/.auth/antiforgery` с X-CSRF и отправить native POST form на `/.auth/end-session`.
Готовый клиент — [bff.ts](samples/MonixOne.OidcProxy.Yarp.SampleHost/ClientApp/src/bff.ts).

## Маршруты и сессии

- Каждый RouteId в `Bff:Clients:<client>:Routes` соответствует маршруту YARP с точным публичным `Match.Hosts`.
- `AllowAnonymous: true` разрешает маршрут без BFF-сессии, например SPA assets или callback приложения.
- Необязательная `CookiePolicy` выбирает политику прикладных cookies. По умолчанию browser credentials удаляются, а защищённый API получает серверный Bearer.
- Маршрут на SPA-хосте без BFF-привязки возвращает 403. Native authorization policies YARP применяются независимо.
- Для `/admin` задайте клиенту `PathBase: "/admin"`, SPA base `/admin/` и callback URI с этим префиксом. Балансировщик сохраняет префикс.
- Для другой SPA добавьте клиента с отдельным origin и OIDC registration. Изменения Bff требуют перезапуска.

Idle timeout по умолчанию — 20 минут, absolute lifetime — 8 часов.
Refresh выполняет BFF; JavaScript токенами не управляет.
Cookie — Secure, HttpOnly, SameSite=Strict. API самостоятельно проверяет issuer, audience и права.

## Подробности и проверка

| Документ | Назначение |
| --- | --- |
| [BR.Admin-Identity](docs/BR-ADMIN-IDENTITY.md) | Готовый профиль и условия подключения Identity/Calendar |
| [Сессии и Redis](docs/SESSIONS.md) | Шифрование, refresh, ошибки и смена ключа |
| [Cookies приложений](docs/COOKIES.md) | Политики для внешних OAuth callbacks |
| [Sample](samples/MonixOne.OidcProxy.Yarp.SampleHost/README.md) | Локальный запуск и интерактивный генератор `/docs` |
| [Проверки](docs/TESTING.md) | Тесты и сборка пакета |

При обновлении до 8.0.0 передавайте в `AddOidcProxy` корневую конфигурацию.
Прежние `OidcProxy.Client` / `OidcProxy.CookiePolicy` metadata перенесите в `Bff:Clients:<client>:Routes`.
Переход с 6.x и замена EncryptionKey требуют нового namespace и повторного входа — [порядок перехода](docs/SESSIONS.md#6-переход-замена-секрета-и-backup).

Основан на [OidcProxy.Net](https://github.com/oidcproxydotnet/OidcProxy.Net). Лицензия LGPL-3.0-only; upstream copyright сохранён.
