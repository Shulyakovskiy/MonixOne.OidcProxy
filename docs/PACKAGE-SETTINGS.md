# Сборка и публикация MonixOne.OidcProxy.Yarp

## Настройки проекта

| Параметр | Значение / источник |
|---|---|
| SDK | `global.json`: 10.0.403, latestPatch, без prerelease |
| Runtime | .NET 10, TargetFramework net10.0 |
| C# | LangVersion latest, nullable и implicit usings включены |
| Качество сборки | TreatWarningsAsErrors, EnableNETAnalyzers, AnalysisLevel latest |
| Воспроизводимость | Deterministic, ContinuousIntegrationBuild, portable PDB |
| Зависимости | Версии централизованы в Directory.Packages.props |
| Test runner | Native Microsoft.Testing.Platform, xUnit v3 |
| Package ID / namespace | MonixOne.OidcProxy.Yarp |
| Метаданные | Title, Description, Tags, Authors, Icon, README, RepositoryUrl, XML API docs |
| Артефакты | nupkg и snupkg, net10.0 DLL/XML и portable PDB |
| Лицензия | LGPL-3.0-only; upstream copyright сохраняется |

Параметры компиляции задаются в `Directory.Build.props`; метаданные и Version — в `src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj`. Зависимости OpenIddict/PostgreSQL/Testcontainers относятся к тестам; пакет использует ASP.NET Core OIDC, YARP, Redis и IdentityModel.

## Локальная сборка

```sh
dotnet restore MonixOne.OidcProxy.Yarp.sln --disable-parallel
dotnet build MonixOne.OidcProxy.Yarp.sln -c Release --no-restore -p:GeneratePackageOnBuild=false
dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release --no-build
dotnet pack src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release --no-build -o artifacts/nuget
```

CI дополнительно собирает React/Vite sample, проверяет using, повторяет concurrency/multi-host сценарии и создаёт publish output sample host. Минимальное число тестов задаётся в `.github/workflows/ci.yml`, чтобы пустой test run завершался ошибкой. Команды и инфраструктура тестов описаны в [TESTING.md](TESTING.md).

## NuGet trusted publishing

`.github/workflows/publish.yml` принимает только ручной `workflow_dispatch`. Job разрешён для main в репозитории `Shulyakovskiy/MonixOne.OidcProxy.Yarp`; он выполняет tests/pack, получает краткоживущий credential через `NuGet/login` и отправляет package вместе с symbols.

Настройте trusted publisher на nuget.org:

| Поле | Значение |
|---|---|
| Owner | Shulyakovskiy |
| Repository | MonixOne.OidcProxy.Yarp |
| Workflow | publish.yml |
| GitHub environment | production |
| Package scope | MonixOne.OidcProxy.Yarp |

GitHub environment `production` должен содержать secret `NUGET_USER` с NuGet username. OIDC job использует `id-token: write`; долгоживущий NuGet API key хранить не требуется. Значения credentials не включайте в конфигурацию репозитория, логи или SPA.

Перед ручным выпуском выполните CI, проверьте подключение потребителя и установите свободную Version в csproj. Убедитесь, что nupkg содержит нужные ID/version, README, icon, license, repository URL/commit и только runtime dependencies. Публикация NuGet-версии необратима; новую сборку выпускают с новой версией.
