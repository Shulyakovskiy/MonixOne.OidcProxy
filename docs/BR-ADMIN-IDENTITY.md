# Проверка перехода BR.Admin-Identity на единый YARP BFF

Дата проверки: 29.10.2026. Цель — удалить Node BFF и подключить SPA к `MonixOne.OidcProxy.Yarp` по стандартному OIDC-контракту. На этом этапе исходники BR.Admin-Identity и BR.Identity не меняются. Пакет в NuGet не опубликован; workflow публикации не запускался.

**Текущая конфигурация не готова к сквозному подключению.** Проверки самого пакета и существующих сервисов не являются выполненной миграцией SPA. Ниже перечислены фактические различия и необходимые подготовительные шаги.

## Что работает сейчас

| Компонент | Фактическое поведение | Основание в исходниках |
|---|---|---|
| BR.Admin-Identity login | React вызывает `/session-api/login/start` и `/session-api/login/confirm`. Node запрашивает OTP grant и меняет его на токены с `client_id=mobile-app` | `src/features/auth/infrastructure/HttpAuthGateway.ts`, `server/features/browser-session/controller.ts`, `identity.ts` |
| Текущая сессия админки | Node хранит credentials в PostgreSQL. Browser получает `__Host-br-admin-session`. `SessionInfo` содержит subject, email, roles и сроки | `server/features/browser-session/sessions.ts`, `src/features/session/domain/SessionInfo.ts` |
| Calendar API | `IdentityGuard` сначала принимает входящий Bearer; при его отсутствии обращается к собственной Node session. JWT проверяется через JWKS по issuer/audience/algorithms/обязательным claims | `server/shared/auth.ts` |
| Browser security админки | Запросы требуют `X-BR-CSRF: 1`; изменяющие запросы требуют точный Origin. CORS в Node BFF отключён | `server/features/browser-session/security.ts` |
| User-management proxy админки | `/session-api/identity/users/*` получает токен из Node session; это не самостоятельный Bearer resource endpoint | `server/features/browser-session/controller.ts` |
| Размещение SPA | Production Vite build использует `/admin/`; gateway снимает этот префикс. Calendar Node API слушает HTTP | `vite.config.ts`, `server/main.ts`, `Dockerfile` |
| BR.Identity OIDC | OpenIddict 7.7.2, code + PKCE S256, refresh/revocation/end-session; access token encryption отключено. Token response содержит дополнительные `ok`/`reason`, а для части первичных входов — `profile`/`userLinks` | `src/API/Features/OpenIddict/OpenIddictServerConfiguration.cs`, `Core/OpenIddictProfileExchangeHandler.cs` |
| Клиенты Identity | `web-app`, `lkb-web`, `mobile-app` создаются public. Confidential clients в текущем manifest — сервисные client-credentials applications; готового confidential SPA BFF client в manifest нет | `src/Storage/DatabaseInitialization/OpenIddictApplicationSeeder.cs` |
| Identity browser session | `/api/v1/auth/email-login/*` и phone-login создают локальную cookie-session. Authorization endpoint без неё вызывает challenge; cookie handler возвращает 401. Login UI в этом slice отсутствует | `Features/Authentication/VerifyEmailOtp`, `Features/OpenIddict/Authorization`, `Features/Di/IdentityFutureRegister.cs` |
| Tenant | User client_id должен присутствовать в `IdentityTenants:Tenants[*]:ClientIds`; неизвестный client не получает корректную user grant область | `IdentityTenantCatalog`, `OpenIddictTenantHandler`, `Storage/Configuration/IdentityTenantOptions.cs` |
| Scopes/resources | API scopes имеют имена сервисов, например `profile-service.full_access`; общий resource — `beetroute-api`. `api.read` из общего sample не является текущим scope BR.Identity | `src/Security.Contracts/OAuthContracts.cs`, `OpenIddictScopeSeeder.cs` |
| Claims | ID token содержит user subject и tenant_id. Роли и внешний subject добавляются в access token. Перечитывание ролей выполняется при выдаче access token | `OpenIddictTenantHandler`, `OpenIddictUserClaimsHandler`, `OpenIddictUserRoleClaims` |
| Identity logout | GET требует валидный id_token_hint. Обработчик отзывает authorizations/tokens subject в своей tenant-области, а не только один BFF ticket | `Features/OpenIddict/Logout/OpenIddictLogoutHandler.cs` |

Это проверка исходного кода и изолированных тестов. Настройки работающего dev/prod окружения, его БД clients, ingress и TLS здесь не подменяются предположениями из appsettings.

## Целевой контракт чистого подключения

```text
SPA любого framework
  → свой HTTPS origin и именованный client внутри общего .NET BFF/YARP host
    → OIDC + изолированная Redis session на SPA client
    → BR.Identity protocol endpoints
    → Bearer resource APIs, включая Calendar API
```

Пакет не должен содержать названия BR-приложений, предопределённые client_id, OTP/passkey grants, business scope lists или legacy SessionInfo. Authority/client/scopes/публичный origin/Redis scope задаёт host; destinations и path transforms задаёт YARP. Один BFF-хост обслуживает много SPA на разных публичных хостах. Каждая имеет AddOidcProxyClient, собственные client_id/cookie/state/session keys; API routes связываются через Match.Hosts и Metadata["OidcProxy.Client"]. Повторное использование пакета другой SPA не требует изменения его кода.

Node `BrowserSessions`, `BrowserIdentity`, session controllers, cookie, credentials storage и их refresh/revocation flow подлежат удалению при реализации перехода. Calendar business API остаётся resource server с собственной авторизацией. При удалении Node BFF из IdentityGuard также удаляются зависимость от BrowserSessions и cookie fallback; входной контракт API становится только Bearer. Сохранение Node session как второго BFF, совместимый facade над `/session-api/*` и browser OAuth-token SDK в целевую схему не входят.

## Что нужно подготовить

1. **Confidential OIDC client.** Зарегистрировать отдельного клиента с secret, code/refresh grants, PKCE, authorization/token/revocation/end-session permissions и точными callback URI. Назначить только необходимые service scopes. Текущий application seeder не предоставляет конфигурируемый список таких BFF registrations. Регистрация не выполнена в рамках этой проверки.
2. **Tenant mapping.** Включить новый client_id в нужный `IdentityTenants` tenant. Передача X-Tenant-Id от SPA для этого не используется.
3. **Browser login на стороне Identity origin.** Существующий OTP UI админки использует mobile grant и не создаёт browser session провайдера. В штатном BFF он не заменяет authorization/PKCE. Доставку login UI и продолжение authorization после browser-session OTP/passkey необходимо проверить отдельно; добавление Identity-специфичного session bridge в пакет не является решением. В исходниках Identity нет AddCors/UseCors, поэтому прямой cross-origin fetch с credentials нельзя считать готовым способом без проверки ingress/браузера.
4. **Claims/UI.** Перевести SPA на claims из BFF `/.auth/me` и профиль из профильного resource API. BFF не копирует roles/email из access token в ID principal и не возвращает дополнительные поля token endpoint `profile`, `userLinks`, `ok`, `reason`. Текущий SessionInfo не совпадает с этим контрактом.
5. **Маршруты.** Подключить Identity user-management и Calendar resource routes непосредственно через конфигурацию YARP. Node user-management proxy зависит от удаляемой сессии и не подходит как backend для нового BFF. Настроить конкретные service scopes/resources, которые требуют эти endpoints.
6. **Путь публикации SPA.** Если сохраняется `/admin`, у соответствующего Bff:Clients:{name} задан PathBase, пакет применяет штатный ASP.NET Core middleware, Vite base совпадает с ним, ingress сохраняет префикс до BFF, а зарегистрированные callback URI включают его. Если ingress уже снял `/admin`, UsePathBase не восстановит его автоматически. Конкретные ingress-изменения пока не выполнены.
7. **HTTPS до API.** Пакет требует явные HTTPS destinations с проверкой сертификатов. Текущий HTTP listener Calendar API требует проверенной TLS-точки перед ним. Нельзя подставить публичный gateway URL, если он вернёт запрос на тот же BFF route: получится proxy loop.
8. **CSRF.** SPA использует единый `X-CSRF: 1` на границе .NET BFF. Legacy X-BR-CSRF и Node browser-session middleware удаляются вместе с Node BFF; второй browser-security слой для совместимости не сохраняется. Calendar API продолжает самостоятельно валидировать Bearer и бизнес-права, без cookie fallback.
9. **Logout и кэш.** Использовать BFF antiforgery + native POST logout с top-level redirect. Прежний AJAX logout 204 не соответствует RP-initiated logout. Очистить query cache при 401 и убрать Node polling/refresh API.
10. **Сроки и роли.** Согласовать product policy: текущий Node flow имеет remember/30 суток и принудительный refresh после изменения ролей. BFF по умолчанию использует session cookie без persistent expiry, idle 20 минут, absolute 8 часов и refresh по сроку access token. Endpoint принудительного refresh отсутствует; запрос /me сам по себе не гарантирует немедленное обновление API roles. Это нельзя выдавать за сохранённое поведение админки.
11. **Бизнес-особенности Identity.** Profile exchange filter сейчас перечисляет конкретные client_id. Для нового BFF client этот дополнительный обмен не запускается. Если он нужен продукту, требуется отдельное решение на стороне Identity/потребителя; пакет не добавляет такой connector. Subject-wide logout Identity также должен быть принят как фактическая политика SSO.

## Что уже проверено

- Пакет: HTTP-тесты на native MTP; реальные HTTP OIDC/YARP/Redis сценарии, rotating refresh, ошибки/таймауты, logout, JWE ID/access tokens и JWKS rollover. Native MTP/xUnit runner выполняет тесты; актуальные команды и состав — [TESTING.md](TESTING.md).
- Текущий BR.Identity: 22 OpenIddict, 16 AuthorizationCodePkce и 11 resource-server/YARP integration tests прошли на его настоящем application pipeline с disposable PostgreSQL/Redis. Исходники сервиса не менялись.
- Текущий BR.Admin-Identity: 131 frontend/Vite tests, TypeScript typecheck и 17 browser-session/Identity server tests прошли. Server tests использовали отдельную временную БД `calendar_compat_test`; исходники приложения не менялись.

**Не выполнен** сквозной browser login новой SPA через новый confidential client в фактическом окружении BR.Identity, поскольку регистрация/tenant/login-origin/ingress ещё не подготовлены. Не проверены real-user OTP/passkey, удаление Node BFF, миграция пользователей/cookie и работа всех Calendar/Identity операций после перехода. Полная совместимость и готовность к публикации пока не подтверждены.

## Критерий завершения проверки перед подключением

После подготовки окружения пройти в настоящем браузере: первый вход без Identity cookie, повторный вход, API scopes/roles, несколько SPA origins/client_id на общем BFF, запрет cookie/state replay между ними, expiry/refresh через две BFF реплики, logout во время refresh и в другой вкладке, ошибки Redis/IdP, отсутствие OAuth credentials в JavaScript/storage/network responses, и возврат в правильный SPA PathBase. Затем проверить календарные и user-management операции с удалённым Node BFF. До этого пакет не публиковать и потребителей не переключать.
