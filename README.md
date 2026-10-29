# MonixOne.OidcProxy.Yarp

[![CI](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/actions/workflows/ci.yml/badge.svg)](https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/actions/workflows/ci.yml)

BFF для SPA на **.NET 10 LTS**, ASP.NET Core OpenID Connect, **YARP** и Redis.
Один NuGet-пакет `MonixOne.OidcProxy.Yarp`: browser получает HttpOnly session cookie, OAuth-токены остаются на сервере.

Один BFF/YARP-хост обслуживает много SPA на разных публичных HTTPS-хостах, с отдельными OIDC clients и сессиями.
Поддерживается стандартный confidential Authorization Code + PKCE flow, включая OpenIddict.

Репозиторий основан на [OidcProxy.Net](https://github.com/oidcproxydotnet/OidcProxy.Net).
Лицензия **LGPL-3.0-only** и upstream copyright сохранены.

Сервисов приложения, authorization server или UI входа пакет не реализует.

Руководства: [проверки](docs/TESTING.md), [подключение BR.Admin-Identity по шагам](docs/BR-ADMIN-IDENTITY.md), [cookies сторонних приложений](docs/COOKIES.md).

---

## Интерактивное руководство для SPA

```sh
cd samples/MonixOne.OidcProxy.Yarp.SampleHost/ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm docs:dev
```

Откройте **http://localhost:4173/docs**.
Для статической документации не нужны BFF, Redis, сертификат или секреты.

- **Пошаговая настройка**: топология → IdP → Redis/ключи → host/YARP → React → проверка.
  Генератор создаёт appsettings/Program.cs без секретов.
  URL и namespace проверяются локально в браузере.
- **Как работает SPA**: интерактивные этапы login, API, refresh и logout, контракт endpoints, ответственность браузера/BFF/provider/API и ограничения при сбоях.

Документация входит в сборку sample по `/docs`.
Полные команды запуска HTTPS SPA/BFF приведены в [sample README](samples/MonixOne.OidcProxy.Yarp.SampleHost/README.md).

---

## Пошаговая настройка

### 1. Топология и клиент провайдера

SPA и BFF должны иметь **один публичный HTTPS origin**, например `https://spa.example.com`.
Resource API доступен BFF по HTTPS и самостоятельно валидирует access token.

Login — обычная top-level навигация на `/.auth/login`.
Параметр `returnUrl` принимает любой локальный URL, включая deep links, query и fragment; по умолчанию возвращает к корню приложения с PathBase.

Logout поддерживает тот же параметр.
Внешние URL, control characters и неоднозначные повторные параметры отклоняются с 400 до начала login/logout.

У провайдера зарегистрируйте confidential web client с client secret, code/refresh grants, PKCE S256 и scopes `openid profile offline_access api.read` (последний — пример scope вашего API).

| Настройка                | portal                                              | workspace                                                  |
| ------------------------ | --------------------------------------------------- | ---------------------------------------------------------- |
| Client ID                | `portal-bff`                                        | `workspace-bff`                                            |
| Redirect URI             | `https://portal.example.com/.auth/login/callback`   | `https://workspace.example.com/app/.auth/login/callback`   |
| Post logout redirect URI | `https://portal.example.com/.auth/signout-callback` | `https://workspace.example.com/app/.auth/signout-callback` |
| Return URL по умолчанию  | `/`                                                 | `/app/`                                                    |

Для **OpenIddict** нужны authorization, token, end-session и revocation endpoint permissions, code/refresh grant permissions, response type `code` и permission для API scope.
Сервер включает `AllowAuthorizationCodeFlow()`, `AllowRefreshTokenFlow()` и PKCE; login/consent, claim destinations и resource assignment принадлежат authorization server.

См. [application permissions](https://documentation.openiddict.com/configuration/application-permissions.html) и [официальные guides](https://documentation.openiddict.com/guides/).

Encrypted access tokens OpenIddict передаются без разбора.
API должен использовать подходящую OpenIddict validation/decryption или introspection.

Signed ID tokens, в том числе signed-and-encrypted при настройке стандартного `TokenDecryptionKey`, проверяются framework/IdentityModel; профиль SPA берётся из ID token, полномочия API — из access token.

### 2. Пакет и конфигурация

```sh
dotnet pack src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release -o artifacts/nuget
```

Эта команда создаёт локальные nupkg/snupkg.
Подключите ProjectReference на проект пакета или установите собранный nupkg из локального feed.

Все версии зависимостей репозитория централизованы в `Directory.Packages.props`.

```json
{
  "Logging": {
    "LogLevel": {
      "Microsoft.AspNetCore.Hosting.Diagnostics": "Warning",
      "Microsoft.AspNetCore.Authentication.OpenIdConnect": "Warning",
      "Yarp.ReverseProxy.Forwarder.HttpForwarder": "Warning"
    }
  },
  "Bff": {
    "ApplicationName": "spa-gateway",
    "KeyNamespace": "web",
    "Clients": {
      "portal": {
        "PublicOrigin": "https://portal.example.com",
        "PathBase": "",
        "SessionIdleTimeout": "00:20:00",
        "SessionAbsoluteTimeout": "08:00:00",
        "OpenIdConnect": {
          "Authority": "https://identity.example.com",
          "ClientId": "portal-bff",
          "Scope": ["openid", "profile", "offline_access", "api.read"]
        }
      },
      "workspace": {
        "PublicOrigin": "https://workspace.example.com",
        "PathBase": "/app",
        "SessionIdleTimeout": "00:20:00",
        "SessionAbsoluteTimeout": "08:00:00",
        "OpenIdConnect": {
          "Authority": "https://identity.example.com",
          "ClientId": "workspace-bff",
          "Scope": ["openid", "profile", "offline_access", "api.read"]
        }
      }
    }
  },
  "ReverseProxy": {
    "Routes": {
      "portal-api": {
        "ClusterId": "portal-api",
        "Match": {
          "Hosts": ["portal.example.com"],
          "Path": "/api/{**all}"
        },
        "Metadata": {
          "OidcProxy.Client": "portal"
        }
      },
      "workspace-api": {
        "ClusterId": "workspace-api",
        "Match": {
          "Hosts": ["workspace.example.com"],
          "Path": "/api/{**all}"
        },
        "Metadata": {
          "OidcProxy.Client": "workspace"
        }
      }
    },
    "Clusters": {
      "portal-api": {
        "Destinations": {
          "primary": {
            "Address": "https://portal-api.example.com/"
          }
        }
      },
      "workspace-api": {
        "Destinations": {
          "primary": {
            "Address": "https://workspace-api.example.com/"
          }
        }
      }
    }
  }
}
```

`Bff__Clients__portal__OpenIdConnect__ClientSecret`, `Bff__Clients__workspace__OpenIdConnect__ClientSecret` и `ConnectionStrings__Redis` передавайте через secret provider/environment.
`ASPNETCORE_ENVIRONMENT` задаёт environment-сегмент Redis.

Секретов в `VITE_*`, SPA bundle или исходниках быть не должно.

### 3. ASP.NET Core host

```csharp
using MonixOne.OidcProxy.Yarp;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(sp => ConnectionMultiplexer.Connect(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.")));
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetRequiredSection("ReverseProxy"));
builder.Services.AddOidcProxy(builder.Configuration.GetRequiredSection("Bff"));

var app = builder.Build();
app.Services.GetRequiredService<ConnectionMultiplexer>();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseOidcProxy();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("{**path}", "index.html").AllowAnonymous();
app.Run();
```

Для SPA под `/app` задайте `Bff:Clients:workspace:PathBase=/app` и Vite base `/app/`.
Ingress сохраняет префикс до BFF.

Callback URI клиента workspace: `https://workspace.example.com/app/.auth/login/callback` и `https://workspace.example.com/app/.auth/signout-callback`.
Пакет выбирает клиента по точному HTTPS host/port, применяет стандартный ASP.NET Core PathBase и выбирает его именованный OIDC handler.

Cookie и OIDC state защищаются разными framework-схемами; Redis sessions изолированы по имени клиента.
Один `AddOidcProxy(configurationSection)` автоматически обходит `Clients` и регистрирует все схемы; EnvironmentName берётся из IHostEnvironment.

Секция `Bff` копируется при регистрации и фиксируется на время жизни процесса.
Изменение clients, origins, PathBase, lifetimes, cookie policies и OIDC credentials/scopes требует перезапуска.
Пересоздание native options использует тот же снимок.
Для программных configure delegates также используйте стабильные значения.
Нативная конфигурация маршрутов YARP остаётся отдельной от этого снимка.

`UseOidcProxy()` подключает routing/authentication/authorization, BFF endpoints и `MapReverseProxy()`.
Вызывайте его до static files и других routing middleware; дополнительный MapReverseProxy не нужен.

Каждый BFF route содержит `Metadata["OidcProxy.Client"]` и точный `Match.Hosts`, совпадающий с PublicOrigin authority.
Wildcard/чужой host или неизвестный client отклоняются при запуске.

Route на зарегистрированном SPA-хосте без client metadata получает 403.
Клиент выбирается конфигурацией, query/header пользователя его не меняет.

Защищённые routes требуют cookie выбранного клиента и `X-CSRF: 1`.
Дополнительные YARP authorization policies применяются штатно.
Отказ policy на управляемом route использует cookie-схему выбранного клиента и возвращает 403.

Явный `AuthorizationPolicy: "anonymous"` подходит для SPA assets/public API: BFF не требует session и не передаёт OAuth token.
Browser Cookie/Authorization и downstream Set-Cookie по умолчанию удаляются; явная CookiePolicy может пропустить изолированные технические cookies приложения.

Маршруты других хостов без BFF metadata сохраняют штатный YARP-контракт.

Match.Path и transforms задаются стандартной YARP configuration: `/api`, `/graphql` или любые другие пути/методы, без списка business endpoints в пакете.
Оставьте `/.auth/*` для технических endpoints клиента.

Для разных SPA bundles настройте отдельные anonymous YARP routes с нужными Hosts; локальный static-file fallback sample показывает один bundle.
Fallback `{**path}` обрабатывает и deep/dotted GET/HEAD paths; API routes имеют собственный приоритет.

При HTTP hop от ingress настройте ForwardedHeaders **до** HTTPS redirect/BFF.
Доверяйте только своим proxies/networks.
PublicOrigin всегда указывает внешний origin браузера.
При повторном HTTPS до Kestrel сохраняйте Host и проверяйте upstream TLS.
Для HTTP hop задайте KnownProxies/KnownIPNetworks и AllowedHosts.

Не отключайте проверку TLS downstream.

Ограничьте категории `Microsoft.AspNetCore.Hosting.Diagnostics` и
`Yarp.ReverseProxy.Forwarder.HttpForwarder` уровнем Warning в конфигурации хоста.
Их информационные сообщения могут содержать query с code/state.
Request/response logging и трассировка также должны исключать credentials.

### 4. Redis и ключи

Один host-owned `ConnectionMultiplexer` используется через DI.
Требуется Redis 6.2+ с GETDEL и Lua.

Общий Data Protection key ring: `{application}:{environment}:{namespace}:data-protection`; sessions: `{application}:{environment}:{namespace}:{clientName}:sessions:{id}`.
Имена clients уникальны, lowercase ASCII.

Сегменты application/environment/namespace нормализуются в lowercase.
Реплики одного BFF используют одинаковые значения; независимые приложения и окружения — разные.

В Redis хранятся защищённые tickets и общий Data Protection key ring.
Настройте TLS/ACL, доступность и persistence/eviction Redis.
Защитите Data Protection master keys независимым сертификатом/KMS.
Например: `AddDataProtection().ProtectKeysWithCertificate(certificate)`.

Шифрование tickets не защищает от полного доступа к Redis, если master keys доступны там же без дополнительной защиты.

Восстановление старого backup может вернуть удалённые записи и старые credentials.
В плане восстановления сбрасывайте область сессий и/или отзывайте grants.

Memory fallback отсутствует.

### 5. SPA

```ts
// Login — навигация, не fetch.
window.location.assign("/.auth/login");

const response = await fetch("/.auth/me", {
  credentials: "same-origin",
  headers: { "X-CSRF": "1", Accept: "application/json" },
});
```

Те же headers нужны для всех proxy API, включая GET.
`401` → очистить кэш пользователя и предложить вход; `403` → запрос отклонён защитой/авторизацией; `503` → временная недоступность.

Не повторяйте изменяющие запросы автоматически.

Sample использует официальный **@tanstack/react-query** для cache/focus/reconnect и polling `/.auth/me` раз в 60 секунд только в видимой authenticated SPA.
Видимая открытая вкладка поддерживает idle-сессию даже без кликов пользователя.
Это активность HTTP-запросов, а не определение присутствия человека.
Polling не продлевает absolute lifetime.

Anonymous/background вкладка не запускает продление по таймеру; браузер может приостанавливать таймеры.
OAuth SDK и browser refresh tokens не требуются.

Logout: отменить UI query, получить `/.auth/antiforgery` с `X-CSRF: 1`, отправить native POST form с выданным framework token на `/.auth/end-session`.
Это позволяет браузеру пройти redirect к провайдеру.

Полный код находится в [bff.ts](samples/MonixOne.OidcProxy.Yarp.SampleHost/ClientApp/src/bff.ts).

---

## Как работает SPA

![SPA, YARP BFF, Redis, OpenID Provider и Resource API](BFF-flow.png)

![Authorization Code + PKCE login с серверной сессией](BFF-login-workflow.png)

| Endpoint                      | Назначение                                                   |
| ----------------------------- | ------------------------------------------------------------ |
| `GET /.auth/login`            | OIDC challenge, локальный returnUrl или корень приложения    |
| `GET /.auth/login/callback`   | Framework state/correlation/nonce validation и обмен code    |
| `GET /.auth/me`               | Claims сессии, без токенов; cookie + `X-CSRF: 1`             |
| `GET /.auth/antiforgery`      | Framework token для logout form; cookie + `X-CSRF: 1`        |
| `POST /.auth/end-session`     | Локальное удаление сессии, best-effort revocation, RP logout |
| `GET /.auth/signout-callback` | Завершение logout, переход `/`                               |
| Настроенные YARP routes       | Cookie + `X-CSRF: 1`; серверный Bearer к API                 |

Framework защищает session cookie; она содержит случайную ссылку, а не OAuth tokens.
Cookie — `__Host-Http-oidcproxy.{clientName}.session`, Secure/HttpOnly, SameSite Strict, host-only, Path `/`.

Correlation/nonce cookies используют штатные настройки для внешнего OIDC callback.
Чужой Origin/Fetch-Site отклоняется.

Нет CORS-контракта для другого origin SPA.

YARP удаляет входящий Authorization и BFF cookies, добавляет server access token на защищённых routes.
Прикладные cookies по умолчанию удаляются; opt-in CookiePolicies изолируют разрешённые имена/пути и привязывают их к cluster.

Destinations фиксируются конфигурацией по HTTPS, запрет на userinfo/query/fragment и отключение certificate validation.
Authenticated ответы имеют `Cache-Control: no-store`.

API самостоятельно проверяет issuer, audience/resource и scope/roles.

### Время жизни и refresh

`SessionIdleTimeout` отсчитывается от последнего принятого защищённого запроса.
Активность учитывается после проверки Origin/CSRF и собственной сессии BFF.
Отказ resource authorization или downstream response не откатывает уже принятую активность.
Продление выполняется через CAS без допуска в половину idle-интервала.
Параллельный запрос не уменьшает срок, уже продлённый более поздней активностью.
TTL всегда ограничен минимумом idle и фиксированного absolute expiry.

Это правило действует и без refresh token до фактического истечения access token.
После его истечения без offline access требуется новый вход.
Статика, anonymous routes и получение antiforgery token не продлевают idle.

Окно refresh равно `min(30 секунд, 10% оставшегося срока access token при получении)`.
Для токенов на 10, 30 и 120 секунд окно составляет 1, 3 и 12 секунд.
Порог сохраняется в серверном поле `RefreshAt` при входе и после refresh.

Прежний Redis ticket v6 без этого поля читается с тем же Data Protection discriminator.
На первой активности порог рассчитывается из оставшегося срока и сохраняется через CAS.
Миграция cookie или сброс key ring для этого не требуются.

Внутри реплики запросы одной client/session ожидают одну общую задачу.
Завершённая запись ожидания удаляется.
Отмена одного ожидающего не отменяет владельца и остальных запросов.
Разные сессии не используют общий lock.

Между репликами владение определяет Redis CAS.
Один локальный ожидающий использует backoff от 50 до 500 мс с jitter.
Snapshot из cookie authentication переиспользуется с CAS-проверкой актуальности.
Claims и tokens после завершения refresh читаются заново.

Серверный бюджет 30 секунд начинается до отправки claim в Redis.
После начала этой попытки отключение браузера не отменяет владение и сохранение результата.
При потере подтверждения BFF перечитывает тот же operation ID.
Без подтверждённого владения redemption не выполняется.

Остановка хоста отменяет серверный бюджет.
Истёкший pending marker завершает сессию без повторного использования refresh token.
Внутренние таймауты инфраструктуры возвращают 503; отмена HTTP-запроса остаётся отменой.
Refresh/code exchange не повторяются при неизвестном исходе.

Отсутствие нового ID/refresh token обрабатывается по стандарту.
Если первичный code exchange уже выдал refresh token, а проверка token response или ID token отклонила вход, BFF пытается отозвать этот известный credential до возврата ошибки; серверная сессия не создаётся.

Logout атомарно выполняет GETDEL текущего ticket, затем очищает cookie и пытается отозвать refresh token с ограниченным таймаутом.
Поздний refresh не восстанавливает удалённую запись и отзывает известный новый credential best effort.

После подтверждённого GETDEL повторный DEL при очистке cookie не отправляется.
Antiforgery/logout не блокируются ожидающим refresh.
Logout одного клиента удаляет только его локальную сессию; IdP может дополнительно завершать другие SSO grants по своей политике.

### Неопределённый результат Redis

Отмена ожидания команды не отменяет уже отправленную команду Redis.
Если SET сессии применён, но подтверждение потеряно, может остаться недоступная запись до её TTL.
Вход возвращает 503 и пытается отозвать известные credentials.
При подтверждённом SET и отменённом callback BFF пытается удалить известную запись.

Если ответ GETDEL потерян, локальная запись может быть уже удалена.
Logout возвращает 503 и не восстанавливает её.
Без ответа BFF не может определить последний набор credentials для отзыва.

Redis и IdP не образуют транзакцию.
При потерянном ответе провайдера новый credential может остаться неизвестным BFF.
Его срок и глобальный отзыв определяет IdP.
Гарантии отсутствия orphan records/grants при недоступной инфраструктуре нет.

**Границы гарантий**

- Redis и IdP не образуют общую транзакцию. Неизвестный результат refresh требует повторного входа.
- Logout не отменяет уже отправленный API-запрос. Мгновенный глобальный отзыв JWT не гарантируется.
- HttpOnly не предотвращает XSS-вызовы API. CSP и безопасность SPA обеспечивает хост.

Профиль безопасности: [RFC 10017](https://datatracker.ietf.org/doc/html/rfc10017) и [OAuth Security BCP](https://www.rfc-editor.org/rfc/rfc9700.html).
Выход использует [RP-initiated logout](https://openid.net/specs/openid-connect-rpinitiated-1_0.html).
Это не заявление о сертификации или отсутствии всех уязвимостей.

---

## Состав одного пакета

| Модуль         | Ответственность                                                 |
| -------------- | --------------------------------------------------------------- |
| Authentication | Стандартные ASP.NET Core OIDC/cookie schemes и protocol events  |
| Sessions       | Protected Redis tickets, CAS refresh, GETDEL logout, revocation |
| Security       | Собственная BFF session, Origin/Fetch-Site и CSRF boundary      |
| Endpoints      | Фиксированный browser HTTP-контракт                             |
| Forwarding     | Стандартные YARP transforms и HTTPS destination validation      |

Public API и namespace: `MonixOne.OidcProxy.Yarp`; `ProxyOptions`, `OidcProxyClientOptions`, `OidcProxyCookiePolicy`, `OidcProxyDefaults`, `OidcProxyRoutes`, `AddOidcProxy`, `AddOidcProxyClient`, `UseOidcProxy`.
OIDC настраивается штатным `OpenIdConnectOptions`, routing — штатным YARP.

---

## Проверка и публикация

Команды, инфраструктура тестов и проверяемые failure-сценарии описаны в [TESTING.md](docs/TESTING.md).
Требуются SDK из `global.json`, Docker, Node 24.15+ и pnpm 12.16.1.

```sh
dotnet restore MonixOne.OidcProxy.Yarp.sln
dotnet build MonixOne.OidcProxy.Yarp.sln -c Release --no-restore -p:GeneratePackageOnBuild=false
dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release --no-build
dotnet pack src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release --no-build -o artifacts/nuget
```

CI выполняет build, HTTP/concurrency/OpenIddict тесты, проверки SPA/документации, pack и сборку sample.
Workflow `.github/workflows/publish.yml` запускается вручную через `workflow_dispatch` на main, использует GitHub environment `production` и NuGet trusted publishing.

Для него требуются secret `NUGET_USER` и trusted-publisher policy с точными repository/workflow/environment.
Параметры job описаны в [publish.yml](.github/workflows/publish.yml).

Перед выпуском проверьте потребителя в целевом окружении и задайте неиспользованную Version; опубликованную NuGet-версию заменить нельзя.

### Security advisories

Проверяйте NuGet/SPA зависимости и [upstream security advisories](https://github.com/oidcproxydotnet/OidcProxy.Net/security/advisories).

[Upstream #373](https://github.com/oidcproxydotnet/OidcProxy.Net/issues/373) сообщает о CVE.
Идентификатор и технические подробности в issue не опубликованы.
Без этих данных нельзя сопоставить issue с конкретной версией или заявить об устранении указанного CVE.
