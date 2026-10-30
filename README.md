<img src="assets/nuget-icon.png" alt="MonixOne.OidcProxy.Yarp logo" width="128" height="128" />

# MonixOne.OidcProxy.Yarp

BFF для SPA на .NET 10: ASP.NET Core OpenID Connect, YARP и Redis.
OAuth-токены хранятся в зашифрованных серверных сессиях; браузер получает HttpOnly cookie.
Native API, их JWT policies и балансировка работают в том же GW.

```csharp
builder.Services.AddOidcProxy(builder.Configuration);
// После builder.Build():
app.UseOidcProxy();
```

В существующем GW observability, CORS и rate limiting подключаются через callback
`UseOidcProxy(pipeline => …)`. Пакет регистрирует YARP и middleware один раз;
без профилей SPA сохраняет native pipeline.

Cookie bridge использует `__Host-Http-`, `Path=/`, Secure/HttpOnly и отсутствие Domain.
Backend Path проверяется сервером. Настройки GW менять не нужно; после обновления всех
реплик незавершённое внешнее OAuth-подключение следует начать заново.

## Единый интерактивный справочник

Откройте [docs/index.html](docs/index.html) в браузере. При просмотре на GitHub скачайте
этот файл и откройте локально. Всё включено в один HTML: сервер, Node и запущенные сервисы не нужны.

- **Подключение:** генератор appsettings, код GW, callbacks, секреты, шаблоны Admin Local/Dev и несколько SPA.
- **Как работает:** интерактивная архитектура, вход, API, refresh, Calendar, logout и порядок middleware.
- **Маршруты:** BFF/native API, PathBase и симулятор решений GW.
- **Сессии и cookies:** Redis, реплики, отказы, миграция и cookie bridge.
- **Проверка и ошибки:** самодостаточные тесты, локальный запуск, CI и диагностика.

Исходник справочника — React в [ClientApp](samples/MonixOne.OidcProxy.Yarp.SampleHost/ClientApp/src/Documentation.tsx).
Обновляйте его и пересобирайте HTML; отдельно редактировать сгенерированный файл не нужно.

```sh
cd samples/MonixOne.OidcProxy.Yarp.SampleHost/ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm docs:dev       # http://localhost:4173/docs
corepack pnpm docs:build     # обновить docs/index.html
corepack pnpm test
corepack pnpm build          # sample SPA и /docs в wwwroot
```

Node/pnpm закреплены в package.json, .NET SDK — в global.json. CI проверяет тесты,
обе сборки и актуальность HTML. Генератор не запрашивает секреты, ничего не сохраняет
и не обращается к вашим сервисам; симулятор не заменяет проверку реального окружения.

Для .NET-тестов нужны Docker и установленный Chromium Playwright. Команды установки
и браузерные сценарии находятся в разделе «Проверка и ошибки» справочника.

Готовые конфигурации BR.Admin-Identity: [appsettings](docs/examples/br-admin-identity/appsettings.json),
[ingress](docs/examples/br-admin-identity/ingress.nginx.conf),
[tenant mapping](docs/examples/br-admin-identity/identity-tenants.fragment.json).
Sample host: [Program.cs](samples/MonixOne.OidcProxy.Yarp.SampleHost/Program.cs).

Основан на [OidcProxy.Net](https://github.com/oidcproxydotnet/OidcProxy.Net).
Лицензия LGPL-3.0-only; upstream copyright сохранён.
