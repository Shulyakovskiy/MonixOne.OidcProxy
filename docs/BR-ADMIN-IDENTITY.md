# Подключение BR.Admin-Identity

Профиль для SPA `/admin/`, Calendar API и BFF на отдельном публичном origin.
Это шаблон подключения; DNS, client registration и фактический вход проверяются в целевом окружении.

## Адреса и файлы

| Параметр | Значение примера |
| --- | --- |
| Публичный origin | `https://admin-dev.intra-beetroute.ru` |
| BFF client / OIDC client_id | `adminidentity` / `br-admin-identity-bff` |
| PathBase / Vite base | `/admin` / `/admin/` |
| Authority / issuer | `https://id-dev.intra-beetroute.ru/` |
| BFF listener | HTTP 8000 за балансировщиком |
| Calendar / SPA backend | `http://stack-srv-admin-identity-dev_admin-identity:8000/` |
| Resource API gateway | `https://gw-dev.intra-beetroute.ru/` |

- [appsettings.json](examples/br-admin-identity/appsettings.json) — раздельные Bff и YARP settings.
- [ingress.nginx.conf](examples/br-admin-identity/ingress.nginx.conf) — публичный HTTPS, внутренний HTTP.
- [identity-tenants.fragment.json](examples/br-admin-identity/identity-tenants.fragment.json) — пример client_id → tenant для mbr/lkb.

Замените service DNS и IP балансировщика `10.0.0.10` реальными значениями.
Backend destination должен отличаться от публичного admin origin, чтобы не создать proxy loop.

## 1. Подготовьте Identity

Зарегистрируйте отдельный confidential client:

- Redirect URI: `https://admin-dev.intra-beetroute.ru/admin/.auth/login/callback`.
- Post logout URI: `https://admin-dev.intra-beetroute.ru/admin/.auth/signout-callback`.
- Code + refresh grants, PKCE S256, response type code.
- Permissions authorization/token/end-session/revocation и scopes из профиля.
- Resource/audience: `beetroute-api`; scopes не заменяют роли пользователя.
- Client_id должен входить в tenant mapping целевого окружения.

Создавайте registration через штатный seeder/административный механизм Identity.
Не меняйте существующие mobile/web clients ради BFF.

Для первого OIDC-входа нужен browser login UI на origin Identity, создающий provider cookie
и продолжающий исходный authorization request. Mobile OTP grant этого не заменяет.
Проверьте этот путь до переключения SPA: пакет не реализует UI входа провайдера.
Также согласуйте profile exchange для нового client_id, необходимые UI claims и SSO-политику logout.

## 2. Подключите пакет и секреты

Используйте короткий [Program.cs sample](../samples/MonixOne.OidcProxy.Yarp.SampleHost/Program.cs):
`AddOidcProxy(builder.Configuration)` и `UseOidcProxy()`.
Пакет собирает YARP, Redis, forwarded headers и клиентов автоматически.

```text
ASPNETCORE_ENVIRONMENT=Development
ConnectionStrings__Redis=<Redis connection>
Bff__EncryptionKey=<постоянный случайный секрет>
Bff__Clients__adminidentity__OpenIdConnect__ClientSecret=<OIDC client secret>
ForwardedHeaders__KnownProxies__0=<IP непосредственного балансировщика>
```

На всех репликах совпадают Redis scope и EncryptionKey. [Сессии и смена ключа](SESSIONS.md).

## 3. Настройте балансировщик

Публичный `/admin/*` направляется в BFF по HTTP 8000 с сохранением Host и префикса `/admin`.
Балансировщик перезаписывает X-Forwarded-Proto=https, X-Forwarded-Host и X-Forwarded-For;
не переписывает Location или Cookie/Set-Cookie. Сетевой доступ к BFF/backend ограничен доверенными компонентами.

BFF обрабатывает эти заголовки до выбора клиента. Внутренние сертификаты и TLS relay не нужны.
Не записывайте query, cookies или tokens в access logs/APM; уровни журналов заданы в профиле.

## 4. Переведите SPA и Calendar

| Действие | Публичный путь |
| --- | --- |
| Login | Навигация `/admin/.auth/login` |
| Session | `GET /admin/.auth/me` с cookie и X-CSRF: 1 |
| Logout | Antiforgery endpoint, затем native POST `/admin/.auth/end-session` |
| Calendar | `/admin/calendar-api/*` |
| User management / resource API | `/admin/api/*` |

SPA должна использовать контракт BFF, а Calendar оставаться Bearer resource API.
Старые `/session-api/*` и Node browser sessions не входят в новую схему.
API самостоятельно проверяют issuer/audience, scopes и роли. Дополнительные данные пользователя UI получает из API.

Для Google Calendar задайте backend:

```text
APP_URL=https://admin-dev.intra-beetroute.ru/admin/calendar
GOOGLE_CALENDAR_REDIRECT_URI=https://admin-dev.intra-beetroute.ru/admin/calendar-api/connections/google/callback
IDENTITY_ISSUER=https://id-dev.intra-beetroute.ru/
IDENTITY_JWKS_URL=https://id-dev.intra-beetroute.ru/.well-known/jwks
IDENTITY_AUDIENCE=beetroute-api
```

Google client secret остаётся в Calendar. Профиль уже привязывает calendar-google policy к start/callback routes.
Только точный GET callback анонимный; Calendar проверяет state/cookie, срок и однократность.
[Контракт прикладных cookies](COOKIES.md).

## Перед переключением

Проверьте browser login с пустыми cookies, доступ Calendar/resource API, Google callback и native logout.
Проверьте повторный вход после logout, две реплики с общими Redis/ключом и отказ доступа без сессии.
Ожидаемые ошибки: 401 без сессии, 403 при нарушении CSRF/авторизации, 503 при недоступности Redis.
Реальное окружение не считается проверенным по результатам локальных автотестов.
