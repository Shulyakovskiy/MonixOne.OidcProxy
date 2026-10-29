# Проверка MonixOne.OidcProxy.Yarp

Правила тестов — [tests/AGENTS.MD](../tests/AGENTS.MD).
Стек: xUnit v3, Shouldly, WebApplicationFactory фактического sample Program, WireMock.Net для управляемых HTTPS fault-сценариев, настоящие Redis/PostgreSQL через Testcontainers.

Внутренние классы пакета не раскрываются тестам и не тестируются напрямую.
Для HTTP нет mocked HttpMessageHandler.

---

## Запуск

Нужны SDK из global.json, Docker с Redis 8/PostgreSQL 17 images и доступ к loopback sockets.
Контейнеры и TLS certificate общие для коллекции; namespace/client ID уникальны для состояния теста.

Production credentials и реальные пользовательские аккаунты не нужны.

```sh
dotnet restore MonixOne.OidcProxy.Yarp.sln
dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release -p:GeneratePackageOnBuild=false
```

Повтор значимых гонок и совместимости:

```sh
dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release --no-build -- --filter-class 'MonixOne.OidcProxy.Yarp.Tests.Refresh*' --filter-class MonixOne.OidcProxy.Yarp.Tests.MultiHostTests --minimum-expected-tests 40
```

Concurrency barriers используют TaskCompletionSource и удержание настоящего HTTP-ответа после выпуска rotating refresh token.
Session time двигается TimeProvider; тесты не используют sleep для ожидания гонки.

Таймауты ограничивают зависшие проверки, а не заменяют синхронизацию.

---

## Проверяемые поведения

### Вход и граница SPA

`SpaBoundaryTests`:

- Code/PKCE, cookie flags и server-only tokens.
- Anonymous 401, CSRF/Origin rejection, очистка browser credentials.
- ID token issuer/audience/nonce/lifetime/signature.
- Отзыв уже выданного refresh token при отклонённом входе.
- Новая session reference при повторном входе.

### Refresh и logout

`RefreshLogoutTests`:

- Две реплики, два multiplexers, 50 запросов и один refresh.
- Logout во время и после refresh.
- Native antiforgery form без ожидания refresh.
- Invalid grant, 429, 5xx и malformed response без retry.
- Caller cancellation после redemption.

`RefreshProtocolTests`:

- Некорректный ID token и смена subject.
- Optional ID/refresh tokens и expired pending marker.
- JWKS rollover и signed-and-encrypted ID tokens.

### Маршруты и несколько SPA

`SpaRoutingTests` и `SpaHostingTests`:

- Любые API пути, методы, query и body.
- Deep links, dotted paths и разные PathBase.
- Callback URI и local returnUrl с query/hash.
- Open redirect и повтор returnUrl отклоняются до logout.

`MultiHostTests`:

- Два origins/client_id на одном BFF.
- Общий OpenIddict и разные providers.
- Изоляция cookie/state, refresh contention и logout.
- Отклонение неверного host/metadata при запуске.
- Anonymous assets и независимые YARP routes.

`GatewayCompositionTests`:

- Нативный HTTP upstream без BFF metadata.
- Запрет HTTP и отключённой проверки сертификата для BFF cluster.

### Redis и прикладные cookies

`SessionBoundaryTests`:

- Redis failure → 503 без proxy.
- Удаление corrupt records и namespace isolation.
- Idle/absolute expiry, callback replay и CSRF logout rejection.

`ApplicationCookieTests`:

- Изолированные namespaces и динамические cookie names.
- Привязка cluster/path и восстановление backend names.
- Очистка cookies, SameSite, lifetime и Partitioned.
- Malformed/duplicate rejection и защита BFF correlation cookie.

### Конфигурация и OpenIddict

`PreparedConfigurationTests` запускает фактический sample Program с документированным JSON.
Проверяются регистрация клиента, Calendar callback cookie и Bearer forwarding.

`OpenIddictFlowTests` использует настоящий OpenIddict, PostgreSQL и HTTPS resource API:

- Authorization/PKCE, token, refresh, revocation и RP logout.
- Encrypted access tokens с официальной validation.
- API scopes/roles и confidential-client boundary.
- Вход при отсутствии provider session.

OpenIddict storage использует официальный EF Core model и настоящий PostgreSQL.
Пакет не имеет собственной SQL-схемы, поэтому тестовая БД создаётся EnsureCreated.

Login fixture устанавливает test-only provider session, далее проходит настоящий OIDC flow; это не проверка пользовательского login/consent UI конкретной Identity-системы.

---

## React и интерактивная документация

```sh
cd samples/MonixOne.OidcProxy.Yarp.SampleHost/ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm test
corepack pnpm build
corepack pnpm audit
```

Vitest/Testing Library проверяют:

- HTTP-контракт и session/error UI.
- Очистку кэша при 401 и остановку запросов после logout.
- Polling/focus/background policy.
- Генерацию конфигурации и отказ для небезопасных URL.
- Callback URI и интерактивные шаги login/refresh/logout/cookies.
  Secret fields, storage или сетевых запросов у документации нет.

Для визуальной проверки: `pnpm docs:dev` → `http://localhost:4173/docs`.
Для проверки реального browser cookie/redirect flow нужен настроенный HTTPS BFF/provider/API, как описано в sample README.

Unit/integration tests не заменяют это окружение и инфраструктурные проверки backup/failover, TLS, secret provider, CSP и observability.

---

## Package и supply chain

```sh
dotnet format style MonixOne.OidcProxy.Yarp.sln --diagnostics IDE0005 --verify-no-changes
dotnet list MonixOne.OidcProxy.Yarp.sln package --vulnerable --include-transitive
dotnet pack src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release --no-build -o artifacts/nuget
```

Проверьте .nupkg и .snupkg: один package ID MonixOne.OidcProxy.Yarp, net10.0, LGPL-3.0-only, README, PNG icon, source repository metadata, XML API docs, отсутствие test/provider connector dependencies.
Audit отражает известные advisory feeds на момент запуска и не доказывает отсутствие неизвестных CVE.

Статус upstream #373 описан в корневом README.
