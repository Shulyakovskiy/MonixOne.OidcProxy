# React / Vite SPA и YARP BFF

Sample использует единственный пакет `MonixOne.OidcProxy.Yarp`, pnpm, React и официальный TanStack Query.
Секреты и OAuth-токены не передаются в JavaScript.

Провайдер и Resource API настраиваются отдельно.

В appsettings уровень HttpForwarder и Hosting.Diagnostics ограничен Warning.
Это исключает информационные сообщения с query code/state.
При добавлении APM/request logging исключите query и credential headers.

---

## Только интерактивная документация

```sh
cd ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm docs:dev
```

**http://localhost:4173/docs** — генератор публичной конфигурации.
Отдельный раздел объясняет login/API/refresh/logout и прикладные cookies.

Для руководства не нужны HTTPS и backend.
Поля остаются в памяти вкладки. Секреты не запрашиваются.

---

## Полный SPA flow

Требуются .NET SDK из `global.json`, Redis 6.2+, настроенный confidential OIDC/OpenIddict client и HTTPS Resource API.
В `appsettings.json` замените example authority/client ID/адрес API, а credentials передайте через secret provider:

- `ConnectionStrings__Redis` — конфигурация host-owned ConnectionMultiplexer;
- `Bff__Clients__spa__OpenIdConnect__ClientSecret` — secret confidential web client;
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

Доверенный development certificate подготовьте стандартным `dotnet dev-certs https --trust`.
PublicOrigin и зарегистрированные URI:

- `Bff:Clients:spa:PublicOrigin`: `https://localhost:5001`;
- login callback: `https://localhost:5001/.auth/login/callback`;
- logout callback: `https://localhost:5001/.auth/signout-callback`.

SPA и `/docs` обслуживаются ASP.NET Core из `wwwroot`.
Для публикации предварительно выполните `pnpm build`, затем `dotnet publish`.

Скомпилированные файлы не хранятся в Git.

### Вариант 2: Vite hot reload по HTTPS

Запустите BFF на `https://localhost:5001`, затем Vite на **https://localhost:5173**.
Экспортируйте доверенный локальный PEM certificate/key и задайте `DEV_HTTPS_CERT`, `DEV_HTTPS_KEY`, `BFF_TARGET=https://localhost:5001`.

Для доверия Node локальному certificate используйте `NODE_EXTRA_CA_CERTS`; TLS validation не отключается.

```sh
cd ClientApp
corepack pnpm dev
```

В этом режиме PublicOrigin и оба зарегистрированных callback URI должны использовать порт **5173**, поскольку браузер работает через Vite.
В YARP route также замените Match.Hosts на localhost:5173.

Proxy сохраняет origin и направляет `/.auth` и `/api` на BFF.
Секреты не передавайте через `VITE_*`.

---

## SPA под путём

PathBase задаётся отдельно каждому BFF client; пакет применяет штатный ASP.NET Core middleware.
Например, для `/admin` соберите `corepack pnpm build --base /admin/`, задайте `Bff:Clients:spa:PathBase=/admin` и включите тот же префикс в зарегистрированные callback URI.

Клиент берёт base из Vite BASE_URL.
Ingress должен передавать префикс до BFF; удаление префикса gateway требует отдельной корректной настройки до переключения.

---

## Что делает клиент

`src/routes.ts` содержит browser routes.
`src/bff.ts` реализует same-origin fetch с `X-CSRF: 1`, чтение claims и native POST logout с ASP.NET antiforgery token.

`src/session.ts` использует официальный `@tanstack/react-query`: focus/reconnect refetch и authenticated polling раз в 60 секунд в видимой вкладке, без автоматических retries.
Открытая видимая вкладка продлевает idle без действий мышью или клавиатурой.
Это контракт HTTP-активности, а не проверка присутствия человека.
В фоне polling отключён; absolute lifetime ограничивает сервер.

При 401 кэш пользователя/API очищается, показывается вход. 503 показывается как недоступность.
OAuth refresh и повтор redemption не выполняются в React.

Перед native form logout текущие queries отменяются, новые действия UI блокируются.

API `/api/status` — пример внешнего маршрута, его реализует ваш Resource API.
Собственного API, Identity UI и обходов проверки TLS sample не содержит.

---

## Несколько SPA на разных хостах

Добавьте записи в `Bff:Clients`; один AddOidcProxy с секцией Bff автоматически регистрирует всех клиентов внутри пакета.
Для каждого клиента нужны PublicOrigin, собственный confidential client_id/secret, разрешённые scopes и точные callback URI.

YARP route указывает тот же host в Match.Hosts и client name в Metadata["OidcProxy.Client"].
Разные bundles обслуживайте host-bound anonymous YARP routes, а resource routes оставьте защищёнными.

Пример двух клиентов приведён в [корневом руководстве](../../README.md).

Session cookie, OIDC state, Redis sessions и refresh/logout изолированы по клиентам.
Одинаковые технические пути `/.auth/*` допустимы на разных хостах.

Неизвестные SPA origins и маршруты без client binding не получают чужую сессию.
Для hot reload под PathBase дополнительно настройте Vite proxy на prefixed auth/API paths; встроенный dev proxy sample показывает корневую SPA.

Секция Bff фиксируется при регистрации. Изменения клиентов, origins, путей, lifetimes и OIDC параметров требуют перезапуска.
