# Sample: React SPA и YARP BFF

Sample использует тот же пакет и конфигурацию, что описаны в [основном руководстве](../../README.md).
`Program.cs` содержит только подключение пакета и раздачу SPA.

## Генератор конфигурации

```sh
cd ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm docs:dev
```

Откройте `http://localhost:4173/docs`.
Страница генерирует appsettings и Program.cs локально; секреты не запрашивает и к вашим серверам не обращается.
Для неё не нужны BFF, Redis и OIDC provider.

## Полный запуск

Нужны SDK из global.json, Redis 6.2+, настроенный confidential OIDC client и Resource API.
Замените example client/Authority/destination в appsettings.json.
Через environment или secret provider задайте:

```text
ConnectionStrings__Redis=<Redis connection>
Bff__EncryptionKey=<постоянный случайный секрет>
Bff__Clients__spa__OpenIdConnect__ClientSecret=<OIDC client secret>
```

Для локального HTTPS зарегистрируйте callback `https://localhost:5001/.auth/login/callback`
и post logout callback `https://localhost:5001/.auth/signout-callback`.

```sh
dotnet dev-certs https --trust
cd ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm build
cd ..
dotnet run --urls https://localhost:5001
```

SPA и `/docs` обслуживаются из wwwroot. В production TLS завершается на балансировщике,
а sample может слушать HTTP: настройте ForwardedHeaders согласно основному руководству.

## Vite hot reload

Запустите BFF на `https://localhost:5001`. Для Vite задайте доверенные PEM certificate/key
в `DEV_HTTPS_CERT` / `DEV_HTTPS_KEY`, `BFF_TARGET=https://localhost:5001` и запустите `corepack pnpm dev` в ClientApp.
При необходимости задайте Node доверенную CA через `NODE_EXTRA_CA_CERTS`.

В этом режиме PublicOrigin, callback URI и Match.Hosts используют `https://localhost:5173`.
Для SPA под путём согласуйте BFF PathBase, Vite base и prefixed auth/API proxy paths.

## Поведение клиента

`src/bff.ts` выполняет same-origin запросы с X-CSRF и native POST logout.
`src/session.ts` использует TanStack Query: focus/reconnect и polling раз в 60 секунд в видимой авторизованной вкладке.
Такой polling продлевает idle-сессию даже без кликов, но не её absolute lifetime.
При 401 клиент очищает пользовательские данные; изменяющие запросы автоматически не повторяет.

Тесты и сборка: [TESTING.md](../../docs/TESTING.md).
