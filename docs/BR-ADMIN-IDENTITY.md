# BR.Admin-Identity через общий YARP BFF: настройка по шагам

Общий BFF обслуживает несколько SPA на разных HTTPS origins.
Для админки профиль выделяет собственный host и сохраняет Vite base `/admin/`.

Файлы ниже готовят конфигурацию окружения.
Регистрация в IdP, настройка ingress и перевод SPA выполняются отдельно.

---

## Шаг 1. Зафиксируйте адреса и топологию

| Параметр                  | Значение профиля / действие                                                                    |
| ------------------------- | ---------------------------------------------------------------------------------------------- |
| SPA/BFF origin            | `https://admin-dev.intra-beetroute.ru` — новое предлагаемое имя; создать DNS и сертификат      |
| SPA base                  | `/admin/`                                                                                      |
| BFF client name           | `adminidentity` — ключ конфигурации пакета                                                     |
| OIDC client_id            | `br-admin-identity-bff` — отдельная новая confidential registration                            |
| Authority / issuer        | `https://id-dev.intra-beetroute.ru/`                                                           |
| Resource API gateway      | `https://gw-dev.intra-beetroute.ru/` — проверить service routes из сети BFF                    |
| Admin backend TLS address | `https://admin-identity-backend.internal.invalid/` — заменить реальным внутренним TLS endpoint |
| Общий BFF listener        | HTTPS 8443 за ingress; имя `shared-spa-bff` в примере заменить service DNS                     |

Замените все адреса `.invalid` реальными DNS именами окружения.
Backend destination должен отличаться от публичного admin origin, иначе возникнет proxy loop.

```text
Browser → admin-dev/admin/* → ingress → общий HTTPS YARP BFF
  → admin backend TLS endpoint → Node Calendar API / static SPA
  → gw-dev/api/* → resource services
  → id-dev/.well-known/* и /connect/* → OpenIddict
```

`gw-dev/admin/` — адрес размещения текущей сборки через старый шлюз.
Здесь выбран отдельный SPA host: текущие gateway routes и другие клиенты остаются на своих адресах.

При добавлении других SPA используйте тот же BFF process, другие PublicOrigin/client_id и собственные client entries.
Все управляемые BFF destinations требуют HTTPS.

Нативные маршруты без BFF metadata сохраняют стандартные возможности YARP, включая HTTP upstream.
Этот профиль не заменяет конфигурацию остальных gateway routes.

---

## Шаг 2. Проверьте discovery из сети BFF

```sh
curl --fail --silent --show-error \
  https://id-dev.intra-beetroute.ru/.well-known/openid-configuration
```

Сверьте значения, включая завершающий `/` issuer:

| Поле                   | Ожидаемый URL                                          |
| ---------------------- | ------------------------------------------------------ |
| issuer                 | `https://id-dev.intra-beetroute.ru/`                   |
| authorization_endpoint | `https://id-dev.intra-beetroute.ru/connect/authorize`  |
| token_endpoint         | `https://id-dev.intra-beetroute.ru/connect/token`      |
| end_session_endpoint   | `https://id-dev.intra-beetroute.ru/connect/logout`     |
| revocation_endpoint    | `https://id-dev.intra-beetroute.ru/connect/revocation` |
| jwks_uri               | `https://id-dev.intra-beetroute.ru/.well-known/jwks`   |

Нужны доверенный TLS, доступ BFF к этим адресам и PKCE S256.
Дополнительные service scopes проверяйте в OpenIddict scope/application registrations: discovery не обязан перечислять все scopes, хранящиеся в БД.

---

## Шаг 3. Подготовьте регистрацию в IdP

Отдельная confidential web application:

| Поле                     | Значение                                                             |
| ------------------------ | -------------------------------------------------------------------- |
| ClientId                 | `br-admin-identity-bff`                                              |
| ClientSecret             | Из secret provider; тот же секрет передать BFF, не SPA               |
| Grant types              | authorization_code, refresh_token                                    |
| PKCE                     | S256, обязателен                                                     |
| Redirect URI             | `https://admin-dev.intra-beetroute.ru/admin/.auth/login/callback`    |
| Post logout redirect URI | `https://admin-dev.intra-beetroute.ru/admin/.auth/signout-callback`  |
| Endpoints                | authorization, token, revocation, end-session                        |
| Response type            | code                                                                 |
| Scopes                   | openid, offline_access и согласованные service scopes из BFF profile |
| Resource/audience API    | beetroute-api                                                        |

Descriptor для административного provisioning-инструмента с официальным OpenIddict API:

```csharp
var descriptor = new OpenIddictApplicationDescriptor
{
    ClientId = "br-admin-identity-bff",
    ClientSecret = secretFromProvider,
    ApplicationType = OpenIddictConstants.ApplicationTypes.Web,
    ClientType = OpenIddictConstants.ClientTypes.Confidential,
    ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
    RedirectUris = { new Uri("https://admin-dev.intra-beetroute.ru/admin/.auth/login/callback") },
    PostLogoutRedirectUris = { new Uri("https://admin-dev.intra-beetroute.ru/admin/.auth/signout-callback") },
    Requirements = { OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange },
    Permissions =
    {
        OpenIddictConstants.Permissions.Endpoints.Authorization,
        OpenIddictConstants.Permissions.Endpoints.Token,
        OpenIddictConstants.Permissions.Endpoints.Revocation,
        OpenIddictConstants.Permissions.Endpoints.EndSession,
        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.ResponseTypes.Code,
        OpenIddictConstants.Permissions.Prefixes.Scope + "profile-service.full_access",
        OpenIddictConstants.Permissions.Prefixes.Scope + "location-service.full_access",
        OpenIddictConstants.Permissions.Prefixes.Scope + "storage-service.full_access"
    }
};
// Apply through your authorized provisioning tool using IOpenIddictApplicationManager.
```

Implicit consent подходит доверенному first-party приложению; для explicit consent провайдеру нужен соответствующий UI.
OpenIddict обрабатывает openid/offline_access отдельно; permission для service scopes должен соответствовать их существующим registrations и resource `beetroute-api`.

В текущем Identity application seeder перечисляет известные client IDs и не поддерживает произвольную секцию `OpenIddict:Applications:BrAdminIdentity`.
Одного добавления такого JSON недостаточно.

Создание новой registration — отдельная административная операция; не меняйте public `mobile-app`/`web-app` на confidential ради BFF и не пишите secret напрямую в таблицу OpenIddict.

Добавьте новый client_id в tenant mapping.
Сохраните существующих клиентов и tenants.

[identity-tenants.fragment.json](examples/br-admin-identity/identity-tenants.fragment.json) содержит пример для `mbr` и `lkb`.
Объедините его с каталогом целевого окружения.
Tenant определяется по client_id.

**Первый вход требует browser login провайдера.** Без Identity cookie authorization handler возвращает challenge/401.
Готового login UI в этом slice нет.
OTP UI админки использует mobile grant и не создаёт provider browser session.

Для OIDC нужен login UI на Identity origin.
Он должен создать сессию через существующий browser login API.
Затем он должен продолжить исходный authorization request.
Это условие не устраняется конфигурацией BFF/ingress.

До его решения первый вход считать работающим нельзя; custom grant/session bridge в пакет не добавляется.

---

## Шаг 4. Подключите BFF profile и секреты

Полная конфигурация: [appsettings.json](examples/br-admin-identity/appsettings.json).
В ней уже заданы client, service scopes, calendar/API/static routes и cookie policy.

Backend `/session-api/*` не входит в новый контракт.

Регистрация хоста остаётся одной:

```csharp
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetRequiredSection("ReverseProxy"));
builder.Services.AddOidcProxy(builder.Configuration.GetRequiredSection("Bff"));
```

Пакет сам обходит Clients.
Scope list из конфигурации заменяет framework defaults: здесь не запрашиваются `api.read`, `mobile-app.full_access` или лишний `profile`.

Пример использует service scopes, которые запрашивает текущая админка; сократите их под фактически доступные функции.

Передайте через secret provider/environment:

```text
ASPNETCORE_ENVIRONMENT=Development
ConnectionStrings__Redis=<Redis connection with TLS/authentication>
Bff__Clients__adminidentity__OpenIdConnect__ClientSecret=<confidential client secret>
Kestrel__Certificates__Default__Path=/run/secrets/bff-tls.pfx
Kestrel__Certificates__Default__Password=<TLS PFX password>
DataProtection__CertificatePath=/run/secrets/bff-data-protection.pfx
DataProtection__CertificatePassword=<DP PFX password>
ReverseProxy__Clusters__admin-backend__Destinations__primary__Address=<real HTTPS backend endpoint>
```

Это имена параметров, не готовый shell-файл со значениями.
Переменные VITE_* для secrets не используйте.

Все реплики BFF должны иметь общий Redis scope и доступ к одним DP decryption keys.
DP-сертификат защищает master keys в Redis; TLS сертификат обслуживает listener, это разные назначения.

Sample поддерживает `DataProtection:CertificatePath/CertificatePassword` через стандартный ProtectKeysWithCertificate.

Для локального запуска sample с этим профилем задайте отдельный content root, чтобы не смешивать его с sample client `spa`:

```sh
dotnet run --project samples/MonixOne.OidcProxy.Yarp.SampleHost --no-launch-profile -- \
  --contentRoot "$(pwd)/docs/examples/br-admin-identity"
```

Сначала заполните реальные upstream URLs, сертификаты и секреты.
Sample здесь проксирует admin SPA через YARP; его собственный React build не используется.

Redis sessions получают scope `br-spa-gateway:development:bff:adminidentity:`, DP key ring — общий для клиентов этого gateway.
В других окружениях задайте соответствующий environment и отдельные credentials.

---

## Шаг 5. Настройте ingress и внутренний TLS

Шаблоны: [ingress.nginx.conf](examples/br-admin-identity/ingress.nginx.conf) и [backend-tls.nginx.conf](examples/br-admin-identity/backend-tls.nginx.conf).
Имена upstream/сертификатов заменить параметрами реального окружения; доступ к BFF/relay ограничить ingress/BFF сетью.

1. Создайте DNS/TLS для нового admin host.
2. Направьте **все** `/admin/*` этого host в общий BFF: SPA assets/pages, `.auth`, API и Google callback. Prefix `/admin` сохраняется. В proxy_pass нет URI/trailing slash, удаляющего его.
3. Сохраняйте Host. Ingress перезаписывает X-Forwarded-* собственными значениями, очищает X-Forwarded-Prefix и не переписывает Cookie/Set-Cookie.
4. В примере ingress обращается к Kestrel по HTTPS с проверкой SAN/CA.
   SampleHost получает настоящий Host и HTTPS.
   Для HTTP hop потребуется штатный ForwardedHeaders middleware **до** HTTPS/BFF.
   Задайте точные KnownProxies/KnownIPNetworks и AllowedHosts.
   Переключатель ASPNETCORE_FORWARDEDHEADERS_ENABLED с доверием всем источникам не используйте.
5. Node Calendar слушает HTTP 8000.
   TLS relay принимает запросы BFF и передаёт их Node по внутренней сети.
   Шаблон блокирует `/session-api` и `/session-api/*`.
   Проверьте service DNS, сеть, SAN сертификата и доверенную CA из контейнера BFF.
6. Не переписывайте Location callback или cookie paths на ingress: BFF формирует публичные OIDC URLs и управляет namespace/path прикладных cookies.
7. Отключите кеширование auth/API/callback ответов.
   В access logs записывайте только путь, без query и headers.
   Authorization code, state, Cookie и tokens не должны попадать в логи.
   В профиле уровни Hosting.Diagnostics и HttpForwarder выставлены в Warning.
   Проверьте также APM и request/response logging хоста.

Существующие `gw-dev` routes к Identity/resource API сохраняются.
Из сети BFF проверьте, что ресурсный destination действительно достигает API, а не возвращает его обратно на admin BFF.

---

## Шаг 6. Настройте Google Calendar как прикладное подключение

У клиента `adminidentity` policy `calendar-google` разрешает только `calendar_google_state` и привязана к `admin-backend`.
Protected POST start получает server Bearer.

Только GET callback объявлен anonymous; BFF session/token ему не передаётся.
На browser cookie policy добавляет `/admin`, namespace, Secure/HttpOnly и host-only scope; backend получает своё исходное имя.

Настройки backend и Google OAuth application должны совпадать:

```text
APP_URL=https://admin-dev.intra-beetroute.ru/admin/calendar
GOOGLE_CALENDAR_REDIRECT_URI=https://admin-dev.intra-beetroute.ru/admin/calendar-api/connections/google/callback
IDENTITY_ISSUER=https://id-dev.intra-beetroute.ru/
IDENTITY_JWKS_URL=https://id-dev.intra-beetroute.ru/.well-known/jwks
IDENTITY_AUDIENCE=beetroute-api
```

Google client ID/secret остаются у Calendar backend.
Его state validation, cookie comparison, TTL и однократность остаются обязательными.

`clearCookie` на callback удаляет ту же namespaced browser cookie.
Другим приложениям добавляйте самостоятельные политики/route bindings по [COOKIES.md](COOKIES.md); connectors в пакете не нужны.

Текущий Calendar использует одно фиксированное имя state cookie: параллельные попытки одного и того же Google подключения могут заменить друг друга.
BFF изолирует разные приложения, но не меняет реализацию OAuth-транзакции Calendar.

Поддержку нескольких одновременных попыток одного подключения проверяйте отдельно; для backend с динамическими correlation names политика поддерживает `prefix*`.

---

## Шаг 7. Согласуйте контракт SPA и resource API

| Действие SPA        | Публичный путь / условие                                                                    |
| ------------------- | ------------------------------------------------------------------------------------------- |
| Login               | Навигация `/admin/.auth/login?returnUrl=<local URL>`                                        |
| Session             | `GET /admin/.auth/me`, credentials same-origin, `X-CSRF: 1`; claims вместо Node SessionInfo |
| Logout              | Получить `/admin/.auth/antiforgery`, затем native POST form `/admin/.auth/end-session`      |
| Calendar            | `/admin/calendar-api/*`, cookie + X-CSRF; backend получает Bearer                           |
| User management     | `/admin/api/v1/user-management/users/*`; путь `/session-api/identity/*` не используется     |
| Другие resource API | `/admin/api/*` согласно действительным gateway routes                                       |
| Google start        | Protected POST с cookie + X-CSRF                                                            |
| Google callback     | Только внешняя навигация к точному GET callback; state/correlation проверяет Calendar       |

Текущая SPA обращается к Node `/session-api/*`, поэтому одной замены ingress недостаточно.
При реализации подключения клиент переводится на этот контракт, Node BrowserSessions/BrowserIdentity/session controllers и cookie fallback в IdentityGuard удаляются; Calendar остаётся Bearer resource API.

Пока эти изменения не выполнены, статический frontend сам по себе не становится новым BFF-клиентом.

ID token Identity содержит sub/tenant_id; roles и внешний subject находятся в access token.
BFF не раскрывает его SPA и не переносит оттуда claims.

UI получает необходимые профиль/права из resource API; API продолжает самостоятельно проверять роли.
User-management endpoints требуют роли разрешённого оператора; service scopes не заменяют роли и не предоставляют доступ автоматически.

`identity-service.intranet_access` предназначен M2M endpoint и здесь не запрашивается.

У BFF default idle 20 минут, absolute 8 часов и session cookie.
Текущий Node remember/30 дней и принудительный refresh ролей не становятся частью нового контракта автоматически.

Дополнительные `profile/userLinks` из token endpoint BFF не возвращает.
Identity profile exchange filter перечисляет известные client IDs: для нового BFF client этот бизнес-hook отдельно согласовать.

Logout Identity отзывает grants subject в tenant, поэтому выход из одного SPA может завершить другие IdP grants, хотя локальные BFF tickets изолированы.

---

## Шаг 8. Проведите приёмку до переключения трафика

1. Проверьте DNS, CA/SAN, Redis, DP keys и доступ к upstream из сети BFF.
   Запуск должен отклонять неизвестные clients/policies и небезопасные BFF destinations.
2. Проверьте первый вход без Identity cookie, повторный login и local returnUrl в `/admin/`.
   Для первого входа необходим provider login UI.
3. Проверьте `/admin/.auth/me`, Calendar и user-management с нужными roles/scopes.
   API без сессии должен вернуть 401, без X-CSRF — 403.
   Убедитесь, что OAuth credentials отсутствуют в JavaScript, storage и ответах BFF.
4. Подключите Google.
   Backend start получает server Bearer без browser Cookie.
   Callback получает только восстановленную `calendar_google_state`.
   Проверьте неправильный state, повтор callback, истечение и удаление cookie.
5. Подключите второй сервис с тем же backend cookie name.
   Проверьте namespace/cluster isolation и отсутствие перекрёстной передачи.
   Для других SPA используйте отдельный origin/client_id.
6. Проверьте expiry/refresh через две BFF replicas и logout во время refresh.
   Проверьте отказ Redis/IdP и очистку UI cache при 401.
   После 5xx/timeout изменяющие запросы нельзя повторять автоматически.
7. Убедитесь, что `/session-api/*` недоступен через новый вход.
   Проверьте assets, runtime-config.js, deep links, API query/body и callback paths.

Рецепты автоматических проверок пакета — [TESTING.md](TESTING.md).
Эта процедура описывает приёмку целевого окружения; конфигурация не является подтверждением выполненного user login, настройки IdP или deployment.
