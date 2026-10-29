# Cookies прикладных сервисов

По умолчанию BFF удаляет browser Cookie и backend Set-Cookie.
Для технической cookie внешнего OAuth-подключения задайте политику у клиента и выберите её в его Routes.

## Конфигурация

Фрагмент дополняет существующего клиента `adminidentity`:

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
        },
        "Routes": {
          "google-start": { "AllowAnonymous": false, "CookiePolicy": "calendar-google" },
          "google-callback": { "AllowAnonymous": true, "CookiePolicy": "calendar-google" }
        }
      }
    }
  }
}
```

`google-start` и `google-callback` — RouteId штатных YARP routes.
Оба указывают на admin-backend; start ограничен POST, callback — GET.
Готовые маршруты: [appsettings.json](examples/br-admin-identity/appsettings.json).

| Настройка | Значение |
| --- | --- |
| ClusterId | Единственный backend cluster для этой политики |
| Names | Разрешённые имена; поддерживается непустой префикс с завершающим `*` |
| Path | Публичный callback path без BFF PathBase; BFF добавляет префикс сам |
| SameSite | Минимальная защита, по умолчанию Lax; более строгий backend SameSite сохраняется |
| MaxAge | Верхняя граница уже существующего persistent срока; session cookie остаётся session cookie |

## Как проходит подключение

1. SPA вызывает protected start с BFF cookie и X-CSRF. Backend получает серверный Bearer.
2. Backend выдаёт разрешённую state/correlation cookie. BFF добавляет namespace клиента/политики,
   фиксирует Path и делает её host-only, Secure, HttpOnly. Значение сохраняется.
3. Внешний provider возвращает браузер на точный anonymous callback.
   BFF восстанавливает исходное имя разрешённой cookie; access token и BFF cookie не передаёт.
4. Backend проверяет state/correlation, срок и однократность операции, затем очищает cookie.
   BFF удаляет соответствующее публичное имя с тем же Path.

Пример имени в браузере: `__Secure-oidcproxy~adminidentity~calendar-google~calendar_google_state`.
Разные политики могут использовать одинаковые backend names.

## Ограничения политики

- Cookie policy управляет заголовками; проверка OAuth state и внешние credentials принадлежат backend.
- Прикладная cookie не заменяет BFF-сессию. BFF session, antiforgery и OIDC cookies не проксируются.
- Неизвестные cookies отбрасываются. Повтор разрешённого публичного имени возвращает 400 до backend.
- Неизвестная политика или неверный ClusterId отклоняются при запуске.
- Некорректный Set-Cookie возвращает 502 без success body. Автоматический повтор изменяющего запроса недопустим.
- Ответы с политикой имеют Cache-Control: no-store. Partitioned и прочие нерегулируемые атрибуты сохраняются.
- Для cross-site POST нужен точный POST callback и явный SameSite=None в политике и backend cookie.
  Отсутствующий SameSite трактуется как Lax и не ослабляется до None.
- Фиксированное имя cookie может перезаписать предыдущую попытку подключения.
  Для параллельных транзакций backend нужны уникальные correlation names; политика поддерживает `prefix*`.
- Logout BFF не отменяет прикладные OAuth-транзакции: их срок и отмену определяет backend.

Полный пример: [BR.Admin-Identity](BR-ADMIN-IDENTITY.md).
