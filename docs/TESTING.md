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
dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release --no-build -- --filter-class 'MonixOne.OidcProxy.Yarp.Tests.Refresh*' --filter-class MonixOne.OidcProxy.Yarp.Tests.MultiHostTests --filter-class MonixOne.OidcProxy.Yarp.Tests.RedisMutationTests --filter-class MonixOne.OidcProxy.Yarp.Tests.SessionLifetimeTests --minimum-expected-tests 62
```

Concurrency barriers используют TaskCompletionSource и удержание настоящего HTTP-ответа после выпуска rotating refresh token.
Session/protocol expiry двигаются согласованным TimeProvider.
Тесты не используют sleep для ожидания гонки.
В нагрузочной проверке интервал 500 мс измеряет число команд; overlap задаётся barriers.

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
- Caller cancellation после redemption и остановка владельца до ответа IdP.

`RefreshLoadTests`:

- 100 authenticated waiters за управляемым cookie-validation barrier.
- Один redemption и отмена одного ожидающего без отмены остальных.
- GET-команды измеряются через INFO commandstats отдельным test-only соединением.
- Учитываются GET внутри CAS Lua, включая проверку переиспользованного snapshot.
- После выпуска ответа проверяются все HTTP outcomes.

Чтобы вывести измерение через standalone MTP runner после сборки:

```sh
dotnet tests/MonixOne.OidcProxy.Yarp.Tests/bin/Release/net10.0/MonixOne.OidcProxy.Yarp.Tests.dll \
  --filter-class MonixOne.OidcProxy.Yarp.Tests.RefreshLoadTests \
  --output Detailed --show-live-output on --show-stdout All --no-ansi --progress off
```

`RefreshProtocolTests`:

- Некорректный ID token, смена subject и проверка исходного auth_time.
- Обновлённые role claims в текущем HTTP authorization решении; штатный 403 при отказе policy.
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

### Redis, lifetime и прикладные cookies

`RedisMutationTests`:

- RESP gate задерживает ответ после исполнения команды настоящим Redis.
- Отключение после claim, потеря его подтверждения и проверка operation ID.
- Запрет redemption, если запись удалена до проверки владения.
- Неопределённый SET, cleanup отменённого callback и ограничение orphan TTL.
- Остановка хоста во время SET с отзывом известных credentials.
- GETDEL timeout → 503; повторного DEL после подтверждения нет.

`SessionLifetimeTests`:

- Точное idle продление до половины интервала и без refresh token.
- Активность при refresh через прежнюю границу idle.
- Absolute timeout и snapshot, истёкший после cookie validation.
- Access tokens на 10, 30 и 120 секунд.
- Чтение прежнего v6 record без RefreshAt.

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
Дополнительно проверяется startup snapshot после изменения source и пересоздания native options.

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
dotnet format whitespace MonixOne.OidcProxy.Yarp.sln --verify-no-changes
dotnet format style MonixOne.OidcProxy.Yarp.sln --diagnostics IDE0005 --verify-no-changes
dotnet list MonixOne.OidcProxy.Yarp.sln package --vulnerable --include-transitive
dotnet pack src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release --no-build -o artifacts/nuget
```

Проверьте .nupkg и .snupkg: один package ID MonixOne.OidcProxy.Yarp, net10.0, LGPL-3.0-only, README, PNG icon, source repository metadata, XML API docs, отсутствие test/provider connector dependencies.
Audit отражает известные advisory feeds на момент запуска и не доказывает отсутствие неизвестных CVE.

Статус upstream #373 описан в корневом README.

---

## Чистый архив исходников

Используйте committed Git tree, а не архивирование всей рабочей папки.
Команда исключает также случайно tracked build outputs и старые NuGet packages.
Нужные PNG схем и package icon сохраняются.
Незакоммиченные staged/working изменения в этот архив не входят.

```sh
mkdir -p artifacts
git archive --format=zip --output=artifacts/source.zip HEAD -- . \
  ':(exclude,glob)**/bin/**' ':(exclude,glob)**/obj/**' \
  ':(exclude,glob)**/node_modules/**' ':(exclude,glob)**/dist/**' \
  ':(exclude,glob)**/wwwroot/**' ':(exclude,glob)**/ClientApp/public/docs-assets/**' \
  ':(exclude,glob)**/coverage/**' ':(exclude,glob)**/TestResults/**' \
  ':(exclude,glob)artifacts/**' ':(exclude,glob)**/.vite/**' \
  ':(exclude,glob)**/.vitest-cache/**' ':(exclude,glob)**/*.tsbuildinfo' \
  ':(exclude,glob)**/*.nupkg' ':(exclude,glob)**/*.snupkg'
```
