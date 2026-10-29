# Сессии, шифрование и Redis

[Настройка BFF](../README.md#пошаговая-настройка) · [Cookies приложений](COOKIES.md) · [Проверки](TESTING.md)

## 1. Что и чем защищается

| Данные | Защита | Хранение |
| --- | --- | --- |
| Серверная сессия: ticket с OAuth-токенами, сроки и состояние refresh | Напрямую `jose-jwt`, ключ `Bff:EncryptionKey` | Redis, JWE Compact Serialization |
| Session cookie со случайной ссылкой, OIDC state, antiforgery | Стандартный ASP.NET Core Data Protection | Браузер; общий framework key ring в Redis |
| Секреты framework key ring | `jose-jwt` с `Bff:EncryptionKey` | Redis, зашифрованный XML descriptor |
| `Bff:EncryptionKey` | Secret provider хоста | Конфигурация процесса, отдельно от Redis |

Data Protection не участвует в шифровании или расшифровке серверных сессий.
Он нужен стандартным cookie/OIDC/antiforgery механизмам ASP.NET Core.
Общий каталог, файловый key ring и сертификат шифрования для пакета не требуются.

## 2. Формат серверной сессии

`SessionStore` сериализует сессию в JSON и вызывает стандартные методы библиотеки:

```csharp
string encrypted = JWT.Encode(json, encryptionKey,
    JweAlgorithm.PBES2_HS256_A128KW, JweEncryption.A256GCM);

string decryptedJson = JWT.Decrypt(encrypted, encryptionKey,
    JweAlgorithm.PBES2_HS256_A128KW, JweEncryption.A256GCM);
```

PBES2-HS256+A128KW выводит ключ из строкового секрета и защищает ключ шифрования содержимого.
A256GCM шифрует содержимое и проверяет его целостность.
Соль, IV, ключ содержимого и authentication tag формирует `jose-jwt`.
При расшифровке ожидаемые алгоритмы заданы явно; алгоритм из входящей строки не выбирается произвольно.

Внутри зашифрованного JSON находятся:

- `Ticket` — стандартный ASP.NET Core authentication ticket в Base64 с claims и OAuth-токенами;
- `AbsoluteExpiresAt`, `IdleExpiresAt` — границы жизни сессии;
- `SessionKey` — полный Redis key, связывающий payload с приложением, окружением, клиентом и session ID;
- `RefreshAt`, `RefreshOperation`, `RefreshDeadline` — данные координации refresh.

После расшифровки `SessionKey` должен совпасть с ключом прочитанной записи.
Перенос корректного JWE в другую сессию, клиент или namespace не авторизует запрос.
Браузер получает только защищённую случайную ссылку; полный ticket и токены остаются на сервере.

PBES2 выполняется при каждом шифровании/расшифровке. Дополнительного кэша расшифрованных сессий между запросами нет.

## 3. Конфигурация и реплики

Передайте через secret provider/environment:

```text
ConnectionStrings__Redis=<Redis connection with TLS/authentication>
Bff__EncryptionKey=<strong random PBES2 password>
```

`EncryptionKey` — обязательная непустая строка. Сгенерируйте сильный секрет один раз,
например `openssl rand -base64 32`, и сохраните его между restart/redeploy.
Строка используется как пароль PBES2; Base64 не декодируется в raw AES key.
Пакет проверяет наличие секрета, но не оценивает его энтропию.
Не используйте для этой настройки OAuth client secret: это отдельные назначения.

Все реплики одного BFF используют одинаковые Redis, `ApplicationName`, `EnvironmentName`,
`KeyNamespace` и `EncryptionKey`. `EnvironmentName` берётся из окружения хоста при регистрации из секции.
Сегменты Redis prefix нормализуются в lowercase; имена клиентов — lowercase ASCII.
Независимым приложениям и окружениям задайте отдельные scope и секреты.
Изменение конфигурации требует перезапуска: BFF фиксирует startup snapshot.

## 4. Ключи и операции Redis

Требуется Redis 6.2+ с GETDEL и Lua. Один host-owned `ConnectionMultiplexer` используется через DI.

| Redis key | Значение | Жизненный цикл |
| --- | --- | --- |
| `{application}:{environment}:{namespace}:{client}:sessions:{id}` | JWE с JSON сессии | TTL = минимум idle и absolute expiry |
| `{application}:{environment}:{namespace}:data-protection` | Общий зашифрованный framework key ring | Без session TTL; сохраняется для cookies/OIDC/antiforgery |

Lua CAS сравнивает точную ранее прочитанную JWE-строку. Изменение сессии создаёт новый JWE;
неизменившееся состояние переиспользует прежнее значение.
Поздний refresh не может восстановить удалённую сессию или затереть более новое состояние.
Logout удаляет запись через GETDEL и использует токены, актуальные в момент удаления.

Redis требует TLS, ACL, persistence и политики eviction, сохраняющей активные сессии и framework key ring.
Потеря key ring делает существующие framework cookies/state нечитаемыми, даже если JWE сессий ещё доступны.
Корневой `EncryptionKey` в Redis не записывается. Framework master keys там хранятся зашифрованными.

## 5. Ошибки и границы гарантий

| Ситуация | Поведение |
| --- | --- |
| `EncryptionKey` отсутствует или состоит из пробелов | Ошибка конфигурации при запуске |
| Найден прежний открытый framework key ring | Ошибка запуска; нужен переход в новый namespace |
| JWE сессии повреждён, зашифрован другим ключом, содержит неверный JSON или `SessionKey` | Сессия отклоняется, запись удаляется через CAS/GETDEL, API возвращает 401 |
| Redis недоступен во время запроса | 503; независимой memory session нет |
| У реплик разные секреты | Они не могут читать общие сессии/cookies; поддержка такого развёртывания отсутствует |

Значения сессий, ключи, токены и cookies не должны попадать в логи.
Проверка наличия `EncryptionKey` не подтверждает его совпадение со всеми ранее зашифрованными записями.

## 6. Переход, замена секрета и backup

Начиная с 7.0.0 прежний Data Protection формат серверных сессий из 6.x больше не читается; автоматического fallback нет.
Для перехода на JWE или замены секрета:

1. Остановите все старые реплики, чтобы они не меняли ту же область Redis другим форматом/ключом.
2. Задайте новый `Bff:KeyNamespace` и согласованный `Bff:EncryptionKey` на всех репликах.
3. Запустите BFF. Пользователям потребуется повторный вход.
4. Удалите прежние session keys и framework key ring после отключения использовавших их реплик.
   При необходимости отзовите старые grants у провайдера и учтите сохранённые backup.

Одновременная работа нескольких ключей и бесшовная ротация не реализованы.
Старый backup способен вернуть удалённые сессии и прежние credentials в тот же Redis key:
аутентифицированное шифрование не обеспечивает защиту от такого отката.
План восстановления должен сбрасывать область сессий и/или отзывать grants.
