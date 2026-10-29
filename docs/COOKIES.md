# Cookies прикладных сервисов через BFF

По умолчанию управляемый YARP route удаляет browser `Cookie` и downstream `Set-Cookie`.
Для OAuth correlation/state добавьте именованную политику у BFF-клиента.
Выберите её в metadata конкретных routes.
Изменять пакет для каждого приложения не требуется.

---

## 1. Опишите разрешённые cookies

Фрагмент конфигурации клиента:

```json
{
  "Bff": {
    "Clients": {
      "adminidentity": {
        "CookiePolicies": {
          "calendar-google": {
            "ClusterId": "admin-backend",
            "Names": ["calendar_google_state"],
            "Path": "/calendar-api/connections/google/callback",
            "SameSite": "Lax",
            "MaxAge": "00:10:00"
          }
        }
      }
    }
  }
}
```

Этот фрагмент дополняет PublicOrigin/PathBase/OpenIdConnect того же клиента.
`AddOidcProxy(configurationSection)` связывает все параметры автоматически.

| Параметр  | Назначение                                                                                                                                      |
| --------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| ClusterId | Единственный YARP cluster, которому разрешены эти cookies                                                                                       |
| Names     | Точные backend names; допустим суффикс `*` после непустого префикса, например `.AspNetCore.Correlation.*` и `.AspNetCore.OpenIdConnect.Nonce.*` |
| Path      | Обязательный URI path внутри клиента; BFF добавляет его PathBase                                                                                |
| SameSite  | Минимальная защита, по умолчанию Lax. Более строгий backend SameSite сохраняется                                                                |
| MaxAge    | Необязательная верхняя граница уже заданных Max-Age/Expires; исходный срок не продлевается, session cookie не становится persistent             |

Cookie names сравниваются с учётом регистра.
Одинокий `*`, wildcard в середине имени, невалидные имена и пути отклоняются при запуске.

Имена политик — lowercase ASCII letters/digits/dots/underscores/hyphens, как имена клиентов.

---

## 2. Привяжите политику к routes

```json
{
  "ReverseProxy": {
    "Routes": {
      "google-start": {
        "ClusterId": "admin-backend",
        "Match": {
          "Hosts": ["admin-dev.intra-beetroute.ru"],
          "Path": "/calendar-api/connections/google/start",
          "Methods": ["POST"]
        },
        "Metadata": {
          "OidcProxy.Client": "adminidentity",
          "OidcProxy.CookiePolicy": "calendar-google"
        }
      },
      "google-callback": {
        "ClusterId": "admin-backend",
        "AuthorizationPolicy": "anonymous",
        "Match": {
          "Hosts": ["admin-dev.intra-beetroute.ru"],
          "Path": "/calendar-api/connections/google/callback",
          "Methods": ["GET"]
        },
        "Metadata": {
          "OidcProxy.Client": "adminidentity",
          "OidcProxy.CookiePolicy": "calendar-google"
        }
      }
    }
  }
}
```

Start остаётся защищённым: BFF session + `X-CSRF: 1`, затем server Bearer к API.
Только точный callback объявляется anonymous: при внешнем redirect у него нет custom CSRF header и может отсутствовать Strict BFF cookie.

На callback YARP не добавляет access token.
Сам сервис обязан проверить state/correlation, срок и однократное использование операции.

Запуск отклоняет неизвестную политику и несовпадающий ClusterId.
CookiePolicy без BFF client metadata также отклоняется.
Route без CookiePolicy сохраняет стандартное поведение BFF и не передаёт прикладные cookies.

---

## 3. Как cookie проходит границу

При client `adminidentity`, policy `calendar-google`, PathBase `/admin`:

| Участок                 | Cookie                                                                            |
| ----------------------- | --------------------------------------------------------------------------------- |
| Backend → BFF           | `calendar_google_state=<opaque>`                                                  |
| BFF → browser           | `__Secure-oidcproxy~adminidentity~calendar-google~calendar_google_state=<opaque>` |
| Browser callback path   | `/admin/calendar-api/connections/google/callback`                                 |
| BFF → выбранный backend | `calendar_google_state=<opaque>`                                                  |

Значение остаётся неизменным.
Namespace в имени изолирует приложения даже при одинаковых backend names; это преобразование HTTP-заголовков, а не формат токена или криптография.

Разделитель `~` недопустим в client/policy IDs и исключает неоднозначность их комбинаций.

BFF пропускает в backend только cookies собственного namespace выбранной политики и только в пределах её Path.
Сырые backend names из browser Cookie не принимаются.

BFF session, antiforgery, OIDC nonce/correlation cookies и cookies других приложений не передаются.
Повтор одного разрешённого публичного имени отклоняется с 400 до отправки backend-запроса.

Ответ разрешённого backend получает host-only cookie без Domain, с фиксированным публичным Path, Secure и HttpOnly.
Неизвестные cookies отбрасываются.
Исходное имя, включая `__Host-...`, восстанавливается только в запросе к выбранному cluster.
Backend не может своим Set-Cookie перезаписать BFF cookie или cookie другой политики.

Дополнительные backend attributes сохраняются, включая Partitioned: BFF не превращает partitioned cookie в обычную.
Domain/Path/Secure/HttpOnly/SameSite регулируются описанной политикой.

При очистке cookie BFF использует тот же namespace и Path, поэтому browser удаляет нужную запись.
Некорректный Set-Cookie на route с политикой возвращает 502 без success body и cookies.

Ответы routes с CookiePolicy имеют `Cache-Control: no-store`, включая anonymous callback.
Повторять изменяющий запрос после ошибки автоматически нельзя.

---

## 4. SameSite и срок

Для top-level GET callback Google обычно подходит Lax.
Для cross-site POST/form_post явно задайте `SameSite: "None"`, разрешите POST на точном callback route и настройте backend также выдавать `SameSite=None`.

BFF сохраняет более строгий исходный SameSite; отсутствие SameSite рассматривается как Lax и не ослабляется до None.
Secure и HttpOnly обязательны во всех вариантах.

MaxAge ограничивает только существующий persistent срок.
Если у backend cookie нет Max-Age/Expires, она остаётся session cookie.

Сервис проверяет срок и однократность OAuth-транзакции.
Cookie policy регулирует только передачу HTTP cookies.

---

## 5. Добавление других приложений

Добавьте другую запись CookiePolicies и привяжите её к routes нужного cluster.
Для динамических correlation names используйте точный префикс с завершающим `*`.

Политики разных сервисов могут разрешать одинаковые имена: browser namespace различается.
Для каждого внешнего callback сохраняйте узкие Path и Methods.

Количество политик и подключений не фиксируется кодом пакета.
Браузерные ограничения количества/размера cookies и HTTP header limits сохраняются.

При фиксированном cookie name новая OAuth-попытка может заменить предыдущую.
Для параллельных транзакций сервису нужны уникальные correlation names.

Разрешайте только нужные технические cookies.
Не добавляйте в allowlist application sessions или OAuth access/refresh-token cookies для обхода BFF-аутентификации.

Cookies, которым нужен доступ из JavaScript, не входят в этот HttpOnly-контракт.
Валидация business OAuth state, внешние credentials, CSP и защита SPA от XSS остаются ответственностью приложения.

Пример полного подключения — [BR.Admin-Identity](BR-ADMIN-IDENTITY.md).

Первичные источники:

- [YARP transforms](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/extensibility-transforms?view=aspnetcore-10.0).
- [Рекомендации YARP по заголовкам](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/header-guidelines).
- [ASP.NET Core SameSite](https://learn.microsoft.com/aspnet/core/security/samesite?view=aspnetcore-10.0).
  YARP рекомендует корректный Forwarded-контракт для генерации URL на backend; изоляция имён здесь дополнительно нужна нескольким приложениям на одном SPA origin.

---

## Logout и бизнес-транзакции

Logout BFF удаляет его серверную сессию и пытается отозвать OAuth refresh token входа.
Он не отменяет автоматически начатые OAuth-подключения прикладных сервисов и не отзывает их state records.

Срок, отмену и однократность таких транзакций определяет backend приложения.
