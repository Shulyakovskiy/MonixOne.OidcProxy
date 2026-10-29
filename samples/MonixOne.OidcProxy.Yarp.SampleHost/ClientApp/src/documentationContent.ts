import { createRoutes } from "./routes";

export const initialSetup = {
  name: "spa",
  origin: "https://spa.example.com",
  pathBase: "",
  authority: "https://identity.example.com",
  clientId: "spa-bff",
  api: "https://api.example.com/",
  application: "customer-portal",
  environment: "Production",
  namespace: "web",
  scopes: "openid profile offline_access api.read",
};
export type Setup = typeof initialSetup;
export type ClientSetup = Pick<Setup, "name" | "origin" | "pathBase" | "authority" | "clientId" | "api" | "scopes">;

export function validateSetup(setup: Setup): string[] {
  const errors: string[] = [];
  for (const [field, label, originOnly] of [
    ["origin", "SPA / BFF origin", true],
    ["authority", "OIDC Authority", false],
    ["api", "Resource API", false],
  ] as const) {
    try {
      const url = new URL(setup[field]);
      if (url.protocol !== "https:" || url.username || url.password || url.search || url.hash ||
          (originOnly && url.pathname !== "/")) throw new Error();
    } catch {
      errors.push(`${label}: нужен абсолютный HTTPS URL без credentials, query и fragment${originOnly ? " и без пути" : ""}.`);
    }
  }
  for (const field of ["application", "environment", "namespace"] as const) {
    if (!/^[a-zA-Z0-9._-]{1,64}$/.test(setup[field]))
      errors.push(`${field}: 1–64 символа ASCII, буквы, цифры, точка, дефис или подчёркивание.`);
  }
  if (!/^[a-z0-9._-]{1,64}$/.test(setup.name)) errors.push("Client name: 1–64 lowercase ASCII letters, digits, dots, underscores or hyphens.");
  if (setup.pathBase) {
    let validPath = false;
    try {
      const parsed = new URL(setup.pathBase, setup.origin);
      validPath = setup.pathBase.startsWith("/") && !setup.pathBase.startsWith("//") &&
        !/[\u0000-\u001f\u007f\\?#]/.test(setup.pathBase) &&
        parsed.origin === new URL(setup.origin).origin &&
        decodeURI(parsed.pathname) === decodeURI(setup.pathBase);
    } catch { /* The format error is shown below, without a network request. */ }
    if (!validPath) errors.push("PathBase: пустая строка для корня или URL-путь без query, fragment, control characters и dot segments.");
  }
  if (!setup.clientId.trim()) errors.push("Client ID обязателен.");
  const scopes = setup.scopes.trim().split(/\s+/);
  if (!scopes.includes("openid")) errors.push("Добавьте scope openid.");
  if (!scopes.includes("offline_access")) errors.push("Для продления сессии добавьте offline_access и разрешите refresh grant у провайдера.");
  return errors;
}

export function validateClients(setup: Setup, others: ClientSetup[]): string[] {
  const clients = [setup, ...others];
  const errors = clients.flatMap(client => validateSetup({ ...setup, ...client }).map(error => `${client.name}: ${error}`));
  if (new Set(clients.map(client => client.name)).size !== clients.length) errors.push("Client names должны быть уникальны.");
  const origins = clients.flatMap(client => {
    try { return [new URL(client.origin).origin]; } catch { return []; }
  });
  if (new Set(origins).size !== origins.length) errors.push("Каждая SPA должна использовать отдельный origin; одинаковые хосты/порты не допускаются.");
  return errors;
}

export function createConfiguration(setup: Setup, others: ClientSetup[] = []): string {
  const clients = [setup, ...others];
  return JSON.stringify({
    Bff: {
      ApplicationName: setup.application,
      KeyNamespace: setup.namespace,
      Clients: Object.fromEntries(clients.map(client => [client.name, {
        PublicOrigin: new URL(client.origin).origin,
        PathBase: new URL(client.pathBase || "/", client.origin).pathname.replace(/\/+$/, ""),
        SessionIdleTimeout: "00:20:00",
        SessionAbsoluteTimeout: "08:00:00",
        OpenIdConnect: {
          Authority: client.authority, ClientId: client.clientId,
          Scope: client.scopes.trim().split(/\s+/),
        },
      }])),
    },
    ReverseProxy: {
      Routes: Object.fromEntries(clients.map(client => [`${client.name}-api`, {
        ClusterId: `${client.name}-resource-api`,
        Match: { Hosts: [new URL(client.origin).host], Path: "/api/{**all}" },
        Metadata: { "OidcProxy.Client": client.name },
      }])),
      Clusters: Object.fromEntries(clients.map(client => [`${client.name}-resource-api`, {
        Destinations: { primary: { Address: client.api } },
      }])),
    },
  }, null, 2);
}

export const hostCode = `using MonixOne.OidcProxy.Yarp;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(sp => ConnectionMultiplexer.Connect(
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.")));
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetRequiredSection("ReverseProxy"));
builder.Services.AddOidcProxy(bff =>
{
    builder.Configuration.GetRequiredSection("Bff").Bind(bff);
    bff.EnvironmentName = builder.Environment.EnvironmentName;
});
foreach (var section in builder.Configuration.GetRequiredSection("Bff:Clients").GetChildren())
    builder.Services.AddOidcProxyClient(section.Key,
        client => section.Bind(client),
        oidc => section.GetRequiredSection("OpenIdConnect").Bind(oidc));

var app = builder.Build();
app.Services.GetRequiredService<ConnectionMultiplexer>();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();
app.UseOidcProxy();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("{**path}", "index.html").AllowAnonymous();
app.Run();`;

export function createSpaCode(pathBase: string) {
  const routes = createRoutes(pathBase);
  return `// Login is a top-level browser navigation, never fetch().
const login = new URL(${JSON.stringify(routes.login)}, window.location.origin);
login.searchParams.set("returnUrl", window.location.pathname + window.location.search + window.location.hash);
window.location.assign(login.pathname + login.search);

// Only the same-origin BFF receives the session cookie.
const response = await fetch(${JSON.stringify(routes.user)}, {
  credentials: "same-origin",
  headers: { "X-CSRF": "1", Accept: "application/json" },
});
if (response.status === 401) {
  // Clear cached user/API data and show a login action.
} else if (!response.ok) {
  throw new Error(\`BFF returned \${response.status}\`);
} else {
  const claims = await response.json();
  // Render claims; there are no OAuth tokens in this response.
}`;
}

export const flows = {
  login: {
    title: "Вход", summary: "Authorization Code + PKCE: SPA инициирует переход, BFF обменивает код по backchannel.",
    steps: [
      ["Браузер → BFF", "Пользователь открывает /.auth/login на origin своей SPA. BFF выбирает зарегистрированный клиент по HTTPS host/port и его PathBase, затем именованную OIDC-схему. ASP.NET Core создаёт state, nonce, PKCE verifier и временные correlation/nonce cookies. Без returnUrl возврат идёт к корню приложения. Локальный returnUrl сохраняет deep link, query и fragment; ASP.NET Core проверяет его и BFF защищает в state.", "В браузере нет client secret или code verifier, доступного JavaScript."],
      ["BFF → OpenID Provider", "Браузер перенаправляется на authorization endpoint из discovery. Передаются client_id, redirect_uri, response_type=code, scope и code_challenge S256. Пользователь входит на стороне провайдера.", "Провайдер проверяет точный redirect URI и регистрацию confidential client."],
      ["Provider → BFF callback", "Провайдер возвращает code и state на /.auth/login/callback. Framework проверяет state и correlation, затем BFF один раз обменивает code с client secret и PKCE verifier.", "Код не передаётся SPA. Не повторяйте обмен после неопределённого сетевого исхода."],
      ["BFF → Redis → браузер", "Framework проверяет ID token: подпись, issuer, audience, lifetime и nonce. Полный ticket с токенами защищается Data Protection и записывается в Redis. Браузеру выдаётся защищённая случайная ссылка в cookie, затем redirect к корню приложения.", "Session cookie: Secure, HttpOnly, SameSite=Strict, Path=/, без Domain. Временные OIDC cookies имеют необходимые framework-настройки для внешнего callback."],
      ["React → /.auth/me", "После загрузки SPA TanStack Query получает claims через same-origin fetch с X-CSRF: 1. Ответ не содержит access_token, refresh_token, id_token или session reference. Anonymous session возвращает 401.", "Claims подходят для UI. Доступ к ресурсам проверяет API по access token."],
    ],
  },
  api: {
    title: "Запрос API", summary: "У каждой SPA свой публичный origin с BFF endpoints; внутренний API получает только серверный Bearer token.",
    steps: [
      ["SPA → /api/…", "fetch использует credentials: same-origin и X-CSRF: 1 даже для GET. Браузер автоматически прикладывает HttpOnly cookie. SPA не добавляет Authorization и не обращается к Resource API напрямую.", "Custom header требует preflight для cross-origin запроса. BFF также отклоняет чужой Origin и Sec-Fetch-Site."],
      ["BFF → Redis", "Проверяется собственная именованная cookie-схема клиента и существование защищённого серверного ticket. Принципал из другой Bearer/Identity middleware не заменяет BFF session. При необходимости BFF координирует refresh.", "Redis недоступен → 503, без memory fallback. Нет сессии → 401; для API нет redirect на HTML login."],
      ["YARP → Resource API", "Проксируются только явно сконфигурированные YARP destinations по HTTPS с проверкой TLS. Входящие Authorization и Cookie удаляются. YARP добавляет access token из серверного ticket. Query/fragment/userinfo в destination запрещены.", "Не стройте destination из URL пользователя и не отключайте проверку сертификатов."],
      ["Resource API → SPA", "API самостоятельно проверяет issuer, audience/resource, scopes и роли. YARP возвращает payload, убирает Set-Cookie конечного сервиса и ставит Cache-Control: no-store. Неуспешная бизнес-операция не повторяется пакетом.", "Уже отправленный запрос может завершиться после logout. Мгновенный отзыв выданного JWT требует механизма со стороны API/провайдера."],
    ],
  },
  refresh: {
    title: "Продление", summary: "JavaScript поддерживает актуальность UI. Refresh token использует только BFF, с координацией через Redis.",
    steps: [
      ["Активная SPA → BFF", "Sample использует официальный @tanstack/react-query: проверка /.auth/me каждые 60 секунд только для authenticated SPA в видимой вкладке, а также после focus/reconnect при устаревании данных. Anonymous вкладка таймер не запускает.", "Это активность сессии: открытая видимая SPA продлевает idle lifetime. Absolute lifetime 8 часов при этом не меняется. В фоне браузер может приостановить таймер."],
      ["Один владелец refresh", "Когда access token истекает через 30 секунд или раньше, BFF атомарно сохраняет pending operation в Redis через compare-and-swap. Конкурентные запросы других вкладок и реплик ждут результат этой операции. У другого клиента отдельная область session keys и собственный refresh.", "In-process SemaphoreSlim не является гарантией между репликами. Пакет использует CAS по серверной записи."],
      ["BFF → token endpoint", "Один владелец отправляет refresh_token grant с confidential credentials. Повторной отправки после timeout, 429, 5xx или invalid_grant нет. После claim отключение браузера не прерывает сохранение результата; операция ограничена 30 секундами и остановкой хоста.", "Нельзя безопасно повторно использовать rotating refresh token после неизвестного исхода обмена."],
      ["BFF → Redis, CAS", "Новые токены проверяются и записываются только если существует та же pending operation и не прошёл её deadline. Новый ID token проверяется, если провайдер его вернул. Если нового refresh token нет, сохраняется предыдущий.", "Logout удалил запись → поздний refresh её не восстанавливает, известный новый refresh token отзывается best effort."],
      ["Ошибка → повторный вход", "invalid_grant или некорректные токены удаляют локальную сессию. Неопределённый сетевой исход закрывает её и возвращает 503 инициирующему запросу; следующий запрос получает 401. Просроченный pending marker после сбоя процесса не позволяет повторный refresh.", "Redis и провайдер не образуют общую транзакцию. Потерянный ответ может оставить неизвестный токен у провайдера до истечения/отзыва grant."],
    ],
  },
  logout: {
    title: "Выход", summary: "Сначала прекращается локальная сессия, затем выполняются отзыв refresh token и RP-initiated logout.",
    steps: [
      ["React → antiforgery endpoint", "SPA отменяет свои query и получает /.auth/antiforgery с X-CSRF: 1. Этот endpoint и logout не ждут refresh access token. Ответ содержит framework requestToken и имя form field.", "GET /.auth/end-session не выполняет выход. Для browser redirect sample отправляет native POST form с antiforgery token."],
      ["BFF → Redis GETDEL", "Авторизованный POST /.auth/end-session атомарно забирает текущий ticket и удаляет запись. Используется токен из записи на момент удаления, включая refresh, завершившийся во время запроса. Очищается cookie выбранного клиента. Локальные сессии других клиентов не удаляются; глобальная SSO-политика logout принадлежит провайдеру.", "После разрешения logout разрыв клиентского соединения не отменяет локальное удаление; Redis операция ограничена таймаутом."],
      ["BFF → revocation endpoint", "BFF пытается отозвать текущий refresh token по backchannel с таймаутом 5 секунд. Ошибка отзыва логируется без токенов. Локальная запись уже удалена, поэтому известная cookie больше не авторизует новые запросы.", "Отзыв best effort. При недоступном провайдере нельзя обещать глобальную мгновенную инвалидизацию всех credentials."],
      ["Браузер → Provider → callback", "BFF инициирует стандартный RP-initiated logout с client_id, id_token_hint и фиксированным post_logout_redirect_uri. Провайдер завершает свою сессию и возвращает браузер на /.auth/signout-callback, затем к корню приложения.", "Зарегистрируйте post-logout URI точно. fetch не выполняет нужную top-level навигацию на страницу провайдера."],
      ["Другие вкладки", "Следующий /.auth/me или API-запрос со старой cookie получает 401. UI удаляет кэш данных пользователя и предлагает вход. Незавершённый refresh не может создать прежнюю запись заново.", "Sample не отправляет токены через BroadcastChannel/localStorage и не использует silent-renew iframe."],
    ],
  },
} as const;
