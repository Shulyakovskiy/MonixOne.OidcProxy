# Настройки по эталону MonixOne.Security.Authentication

Проверено 29.10.2026 по файлам эталонного пакета и GitHub API. Новый repository/package ID — `Shulyakovskiy/MonixOne.OidcProxy.Yarp` / `MonixOne.OidcProxy.Yarp`.

| Настройка | Состояние |
|---|---|
| TargetFramework, nullable, implicit usings, LangVersion | net10.0, enabled, enabled, latest |
| Warnings/analyzers | TreatWarningsAsErrors, EnableNETAnalyzers, AnalysisLevel latest, AnalysisMode Default |
| Воспроизводимая сборка | Deterministic, ContinuousIntegrationBuild, portable PDB |
| Central package management | Directory.Packages.props, версии только в одном месте |
| Native test runner | Microsoft.Testing.Platform в global.json, UseMicrosoftTestingPlatformRunner/TestingPlatformDotnetTestSupport, xunit.v3 и VS runner 4.0.0 |
| Защита от пустого test run | CI требует минимум 51 server tests; отдельный повтор refresh — минимум 22 |
| Package metadata | Title, Description, Tags, Authors, Icon, README, RepositoryUrl/type, PublishRepositoryUrl, XML documentation |
| Symbols | IncludeSymbols, snupkg |
| CI/build/pack | ubuntu-24.04, restore --disable-parallel, build без build servers/-m:1, GeneratePackageOnBuild=false при CI build, pack отдельного проекта |
| GitHub ruleset | `main protected` активирован с теми же conditions и правилами deletion/non_fast_forward, что в эталоне; bypass actors отсутствуют. Обязательные reviews/status checks этим ruleset не добавляются |
| Repository/Actions | Issues включены; merge-настройки совпадают с эталоном; workflow permissions read, PR approve запрещён |
| Production environment | Существует, как в эталоне; protection rules и deployment branch policy отсутствуют. Secret NUGET_USER присутствует; его значение API не возвращает и оно не копировалось |
| Publish workflow | `.github/workflows/publish.yml`, production environment, NuGet/login и краткоживущий OIDC credential, push nupkg + symbols. Только ручной workflow_dispatch для main; автоматическая публикация после merge отключена |

Сознательно сохранены отличия, необходимые этому пакету:

- SDK закреплён на 10.0.403/latestPatch без prerelease; эталон использует 10.0.100/latestFeature. Старый SDK эталона не понижает текущую проверенную сборку.
- Версии собственных dependencies соответствуют BFF/YARP/Redis и не заменяются нерелевантными JWT resource-server/M2M dependencies эталона.
- LGPL-3.0-only и upstream authors/copyright сохранены; MIT-лицензия другого проекта не переносится на производный код.
- GitHub Actions закреплены на проверенных commit SHA; дополнительные проверки SPA/документации и настоящих Redis/PostgreSQL нужны этому проекту.
- Package version 6.0.0 ещё не опубликована. Новый NuGet package ID проверен через registry: HTTP 404 на index.json. NuGet trusted-publisher policy для переименованного репозитория и файла publish.yml не проверена через авторизованный NuGet UI.

Публикация не запускалась. Перед ручным запуском необходимы завершённая проверка совместимости и корректная trusted-publishing policy нового репозитория. Состояние перехода BR.Admin-Identity — [отдельный отчёт](BR-ADMIN-IDENTITY.md).
