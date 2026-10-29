# Проверка MonixOne.OidcProxy.Yarp

Правила тестов — [tests/AGENTS.MD](../tests/AGENTS.MD). Стек: xUnit v3, Shouldly, WebApplicationFactory фактического sample Program, WireMock.Net для управляемых HTTPS fault-сценариев, настоящие Redis/PostgreSQL через Testcontainers. Внутренние классы пакета не раскрываются тестам и не тестируются напрямую. Для HTTP нет mocked HttpMessageHandler.

## Запуск

Нужны SDK из global.json, Docker с Redis 8/PostgreSQL 17 images и доступ к loopback sockets. Контейнеры и TLS certificate общие для коллекции; namespace/client ID уникальны для состояния теста. Production credentials и реальные пользовательские аккаунты не нужны.

```sh
dotnet restore MonixOne.OidcProxy.Yarp.sln
dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release -p:GeneratePackageOnBuild=false
```

Повтор значимых гонок и совместимости:

```sh
dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release --no-build -- --filter-class 'MonixOne.OidcProxy.Yarp.Tests.Refresh*' --filter-class MonixOne.OidcProxy.Yarp.Tests.MultiHostTests --minimum-expected-tests 40
```

Concurrency barriers используют TaskCompletionSource и удержание настоящего HTTP-ответа после выпуска rotating refresh token. Session time двигается TimeProvider; тесты не используют sleep для ожидания гонки. Таймауты ограничивают зависшие проверки, а не заменяют синхронизацию.

## Проверяемые поведения

| Набор | Гарантии и отказные сценарии |
|---|---|
| SpaBoundaryTests | Code/PKCE, cookie flags, server-only tokens, anonymous 401, CSRF/Origin rejection, stripping browser Bearer/Cookie и downstream Set-Cookie, ID token issuer/audience/nonce/lifetime/signature, смена session reference при повторном входе, фиксированный return URL |
| RefreshLogoutTests | Две реплики/два multiplexers, 50 конкурентных запросов и один refresh; logout во время/после refresh; native antiforgery form без блокировки refresh; invalid_grant/429/5xx/malformed без retry; caller cancellation после redemption |
| RefreshProtocolTests | Ошибочный refresh ID token, смена subject, optional ID/refresh tokens, expired pending marker, JWKS signing-key rollover, signed-and-encrypted ID tokens со стандартной расшифровкой |
| SpaRoutingTests | Произвольные API пути/методы/query/body, SPA deep links и dotted paths, local returnUrl с query/hash, open-redirect/duplicate rejection, сохранение session при неверном logout target |
| SpaHostingTests | Разные стандартные PathBase, prefixed callback URI, login/API/logout и возврат в корень SPA |
| SessionBoundaryTests | Redis connection failure → 503 без proxy, namespace isolation, idle/absolute expiration без refresh, callback replay, GET/CSRF logout rejection |
| MultiHostTests | Один host, два SPA origins/client_id и PathBase; общий OpenIddict и разные providers; cookie/state replay rejection; refresh contention и logout изолированы; invalid host/metadata startup rejection; anonymous assets и независимые YARP routes |
| OpenIddictFlowTests | Настоящий OpenIddict + PostgreSQL: authorization/PKCE/token/refresh/revocation/RP logout и HTTPS resource API с официальной validation; encrypted access tokens; scopes/roles API; confidential-client boundary и отсутствие provider session |

OpenIddict storage использует официальный EF Core model и настоящий PostgreSQL. Пакет не имеет собственной SQL-схемы, поэтому тестовая БД создаётся EnsureCreated. Login fixture устанавливает test-only provider session, далее проходит настоящий OIDC flow; это не проверка пользовательского login/consent UI конкретной Identity-системы.

## React и интерактивная документация

```sh
cd samples/MonixOne.OidcProxy.Yarp.SampleHost/ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm test
corepack pnpm build
corepack pnpm audit --prod
```

Vitest/Testing Library проверяют HTTP-контракт клиента, session/error UI, очистку кэша при 401, polling/focus/background policy и остановку после logout, генератор публичной конфигурации, отказ для небезопасных URL, callback URI и интерактивные шаги refresh/logout. Secret fields, storage или сетевых запросов у документации нет.

Для визуальной проверки: `pnpm docs:dev` → `http://localhost:4173/docs`. Для проверки реального browser cookie/redirect flow нужен настроенный HTTPS BFF/provider/API, как описано в sample README. Unit/integration tests не заменяют это окружение и инфраструктурные проверки backup/failover, TLS, secret provider, CSP и observability.

## Package и supply chain

```sh
dotnet format style MonixOne.OidcProxy.Yarp.sln --diagnostics IDE0005 --verify-no-changes
dotnet list MonixOne.OidcProxy.Yarp.sln package --vulnerable --include-transitive
dotnet pack src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release --no-build -o artifacts/nuget
```

Проверьте .nupkg и .snupkg: один package ID MonixOne.OidcProxy.Yarp, net10.0, LGPL-3.0-only, README, PNG icon, source repository metadata, XML API docs, отсутствие test/provider connector dependencies. Audit отражает известные advisory feeds на момент запуска и не доказывает отсутствие неизвестных CVE. Статус upstream #373 описан в корневом README.
