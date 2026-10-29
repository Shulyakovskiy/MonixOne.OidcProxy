# React / Vite SPA и YARP BFF

Sample использует единственный пакет `MonixOne.OidcProxy.Yarp`, pnpm, React и официальный TanStack Query. Секреты и OAuth-токены не передаются в JavaScript. Провайдер и Resource API настраиваются отдельно.

## Только интерактивная документация

```sh
cd ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm docs:dev
```

**http://localhost:4173/docs** — пошаговый генератор публичной конфигурации и отдельный разбор login/API/refresh/logout. HTTPS и backend для статического руководства не нужны. Поля остаются в памяти вкладки; секреты не запрашиваются.

## Полный SPA flow

Требуются .NET SDK из `global.json`, Redis 6.2+, настроенный confidential OIDC/OpenIddict client и HTTPS Resource API. В `appsettings.json` замените example authority/client ID/адрес API, а credentials передайте через secret provider:

- `ConnectionStrings__Redis` — конфигурация host-owned ConnectionMultiplexer;
- `OpenIdConnect__ClientSecret` — secret confidential web client;
- `ASPNETCORE_ENVIRONMENT` — область Redis для окружения.

### Вариант 1: одна production-подобная точка входа

```sh
cd ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm test
corepack pnpm build
cd ..
dotnet run --urls https://localhost:5001
```

Доверенный development certificate подготовьте стандартным `dotnet dev-certs https --trust`. PublicOrigin и зарегистрированные URI:

- `Bff:PublicOrigin`: `https://localhost:5001`;
- login callback: `https://localhost:5001/.auth/login/callback`;
- logout callback: `https://localhost:5001/.auth/signout-callback`.

SPA и `/docs` обслуживаются ASP.NET Core из `wwwroot`. Для публикации предварительно выполните `pnpm build`, затем `dotnet publish`. Скомпилированные файлы не хранятся в Git.

### Вариант 2: Vite hot reload по HTTPS

Запустите BFF на `https://localhost:5001`, затем Vite на **https://localhost:5173**. Экспортируйте доверенный локальный PEM certificate/key и задайте `DEV_HTTPS_CERT`, `DEV_HTTPS_KEY`, `BFF_TARGET=https://localhost:5001`. Для доверия Node локальному certificate используйте `NODE_EXTRA_CA_CERTS`; TLS validation не отключается.

```sh
cd ClientApp
corepack pnpm dev
```

В этом режиме PublicOrigin и оба зарегистрированных callback URI должны использовать порт **5173**, поскольку браузер работает через Vite. Proxy сохраняет origin и направляет `/.auth` и `/api` на BFF. Секреты не передавайте через `VITE_*`.

## Что делает клиент

`src/routes.ts` содержит browser routes. `src/bff.ts` реализует same-origin fetch с `X-CSRF: 1`, чтение claims и native POST logout с ASP.NET antiforgery token. `src/session.ts` использует официальный `@tanstack/react-query`: focus/reconnect refetch и authenticated polling раз в 60 секунд в видимой вкладке, без автоматических retries. В фоне polling отключён; absolute lifetime ограничивает сервер.

При 401 кэш пользователя/API очищается, показывается вход. 503 показывается как недоступность. OAuth refresh и повтор redemption не выполняются в React. Перед native form logout текущие queries отменяются, новые действия UI блокируются.

API `/api/status` — пример внешнего маршрута, его реализует ваш Resource API. Собственного API, Identity UI и обходов проверки TLS sample не содержит.
