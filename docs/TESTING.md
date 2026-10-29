# Проверки пакета

Правила тестов: [tests/AGENTS.MD](../tests/AGENTS.MD).
Нужны SDK из global.json, Docker, Node и pnpm из package.json.
Тесты используют WebApplicationFactory, WireMock и реальные Redis/PostgreSQL через Testcontainers.
Production credentials не нужны.

## Форматирование C#

Единственный форматтер — CSharpier 1.3.0, закреплённый в `.config/dotnet-tools.json`.
Общие настройки находятся в `.editorconfig`; 100 символов — ориентир переноса строки.
Выполняйте команды из корня репозитория; нужны GNU Make, Bash, Git и `xargs`
(macOS/Linux или Git Bash с установленным GNU Make):

```sh
make tools-restore
make format
make format-check
```

`make` выводит справку. Цели `format` и `format-check` используют восстановленный
локальный инструмент; CI вызывает те же цели Makefile.
Локальная команда и отдельная проверка CI охватывают только C# текущего решения:
исходники, тесты и sample, включая новые файлы до `git add`.
XML, конфиги и React не передаются форматтеру. CSharpier учитывает `.gitignore`
и сам пропускает стандартный сгенерированный код; дополнительные исключения не требуются.
Проверка `check` не меняет файлы и возвращает ненулевой код при нарушениях.
Не запускайте `dotnet format whitespace` или полный `dotnet format` поверх CSharpier.
Существующая точечная проверка IDE0005 в CI отвечает за неиспользуемые `using`.

В Rider установите официальный плагин CSharpier и используйте `Reformat with CSharpier`
для выбранных `.cs`/`.csx`, чтобы сохранить область внедрения.
В `Tools | Actions on Save` отключите встроенный `Reformat and Cleanup Code` для C#,
а также конкурирующие `Reformat code`/`Optimize imports`, если они включены.
Плагин [выбирает версию из локального tool manifest](https://github.com/belav/csharpier/blob/main/Src/CSharpier.Rider/README.md#csharpier-version);
если манифеста нет, он ищет глобальную установку. Версия самого плагина независима
от закреплённой версии форматтера.

## .NET

```sh
dotnet restore MonixOne.OidcProxy.Yarp.sln
dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release -p:GeneratePackageOnBuild=false
```

Для изменений маршрутов или HTTP pipeline сначала выполните целевые тесты:

```sh
dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release -p:GeneratePackageOnBuild=false -- --filter-class MonixOne.OidcProxy.Yarp.Tests.GatewayCompositionTests --filter-class MonixOne.OidcProxy.Yarp.Tests.TlsTerminationTests --filter-class MonixOne.OidcProxy.Yarp.Tests.MultiHostTests
```

| Область | Что проверяется |
| --- | --- |
| OIDC и SPA | Code/PKCE, проверка ID token, CSRF/Origin, cookie flags, server-only tokens |
| Маршруты | Bff-привязки, изоляция клиентов, native YARP policies, HTTP backend и доверенные forwarded headers |
| Сессии | JWE, срок жизни, общий key ring, повреждённые записи и изоляция Redis scope |
| Refresh/logout | Конкурентные запросы и реплики, cancellation, неизвестный результат Redis/IdP, отсутствие повторного redemption |
| Прикладные cookies | Namespace/Path, SameSite/expiry, удаление cookie, отклонение неправильных заголовков |
| Интеграция | Настоящий OpenIddict/PostgreSQL и документированный профиль BR.Admin-Identity |

Конкурентные сценарии синхронизируются barriers, а не sleeps.
Изменения безопасности и общего pipeline завершаются полным прогоном проекта.

## React и генератор документации

```sh
cd samples/MonixOne.OidcProxy.Yarp.SampleHost/ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm test
corepack pnpm build
```

Проверяются генерация и валидация конфигурации, callbacks/PathBase, несколько SPA,
раздельные Bff/YARP settings и клиентский session/logout flow.
После изменения разметки откройте `/docs` и проверьте навигацию, код и мобильную ширину.

## NuGet

```sh
dotnet pack src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release -o artifacts/nuget
```

Проверьте nupkg/snupkg: package ID, net10.0, LGPL-3.0-only, README, icon, XML API docs
и отсутствие тестовых dependencies. Публикация — отдельная операция.

Автотесты не подтверждают конкретное развёртывание. Перед переключением трафика проверьте
реальный browser login/logout, backend Bearer validation и работу через балансировщик.
