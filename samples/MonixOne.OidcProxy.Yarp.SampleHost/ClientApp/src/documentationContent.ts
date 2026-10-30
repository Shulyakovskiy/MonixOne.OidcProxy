import { createRoutes } from "./routes";

export const initialSetup = {
  name: "spa",
  origin: "https://spa.example.com",
  pathBase: "",
  authority: "https://identity.example.com",
  clientId: "spa-bff",
  api: "http://resource-api:8000/",
  apiPath: "/api",
  application: "gateway-service",
  environment: "Development",
  namespace: "bff",
  redisKey: "RateLimiting:RedisConnectionString",
  scopes: "openid profile offline_access api.read",
};
export type Setup = typeof initialSetup;
export type ClientSetup = Pick<
  Setup,
  | "name"
  | "origin"
  | "pathBase"
  | "authority"
  | "clientId"
  | "api"
  | "apiPath"
  | "scopes"
>;

export function validateSetup(setup: Setup): string[] {
  const errors: string[] = [];
  for (const [field, label, originOnly] of [
    ["origin", "SPA / BFF origin", true],
    ["authority", "OIDC Authority", false],
    ["api", "Resource API", false],
  ] as const) {
    try {
      const url = new URL(setup[field]);
      if (
        (url.protocol !== "https:" &&
          !(field === "api" && url.protocol === "http:")) ||
        url.username ||
        url.password ||
        url.search ||
        url.hash ||
        (originOnly && url.pathname !== "/")
      )
        throw new Error();
    } catch {
      errors.push(
        `${label}: нужен абсолютный ${field === "api" ? "HTTP/HTTPS" : "HTTPS"} URL без credentials, query и fragment${originOnly ? " и без пути" : ""}.`,
      );
    }
  }
  for (const field of ["application", "environment", "namespace"] as const) {
    if (!/^[a-zA-Z0-9._-]{1,64}$/.test(setup[field]))
      errors.push(
        `${field}: 1–64 символа ASCII, буквы, цифры, точка, дефис или подчёркивание.`,
      );
  }
  if (!/^[a-z0-9._-]{1,64}$/.test(setup.name))
    errors.push(
      "Client name: 1–64 lowercase ASCII letters, digits, dots, underscores or hyphens.",
    );
  if (setup.pathBase) {
    let validPath = false;
    try {
      const parsed = new URL(setup.pathBase, setup.origin);
      validPath =
        setup.pathBase.startsWith("/") &&
        !setup.pathBase.startsWith("//") &&
        !/[\u0000-\u001f\u007f\\?#]/.test(setup.pathBase) &&
        parsed.origin === new URL(setup.origin).origin &&
        decodeURI(parsed.pathname) === decodeURI(setup.pathBase);
    } catch {
      /* The format error is shown below, without a network request. */
    }
    if (!validPath)
      errors.push(
        "PathBase: пустая строка для корня или URL-путь без query, fragment, control characters и dot segments.",
      );
  }
  if (!setup.clientId.trim()) errors.push("Client ID обязателен.");
  if (
    !/^\/[a-zA-Z0-9/_-]+$/.test(setup.apiPath) ||
    setup.apiPath.endsWith("/") ||
    setup.apiPath.includes("//")
  )
    errors.push(
      "API path: абсолютный префикс без trailing slash, query и wildcard.",
    );
  if (
    !/^[A-Za-z_][A-Za-z0-9_]*(?::[A-Za-z_][A-Za-z0-9_]*)+$/.test(setup.redisKey)
  )
    errors.push(
      "Redis key: путь конфигурации, например RateLimiting:RedisConnectionString.",
    );
  const scopes = setup.scopes.trim().split(/\s+/);
  if (!scopes.includes("openid")) errors.push("Добавьте scope openid.");
  if (!scopes.includes("offline_access"))
    errors.push(
      "Для продления сессии добавьте offline_access и разрешите refresh grant у провайдера.",
    );
  return errors;
}

export function validateClients(setup: Setup, others: ClientSetup[]): string[] {
  const clients = [setup, ...others];
  const errors = clients.flatMap((client) =>
    validateSetup({ ...setup, ...client }).map(
      (error) => `${client.name}: ${error}`,
    ),
  );
  if (new Set(clients.map((client) => client.name)).size !== clients.length)
    errors.push("Client names должны быть уникальны.");
  const origins = clients.flatMap((client) => {
    try {
      return [new URL(client.origin).origin];
    } catch {
      return [];
    }
  });
  if (new Set(origins).size !== origins.length)
    errors.push(
      "Каждая SPA должна использовать отдельный origin; одинаковые хосты/порты не допускаются.",
    );
  return errors;
}

export function createConfiguration(
  setup: Setup,
  others: ClientSetup[] = [],
  options: { native?: boolean; forwardCookies?: boolean } = {},
): string {
  const clients = [setup, ...others];
  return JSON.stringify(
    {
      AllowedHosts: clients
        .map((client) => new URL(client.origin).hostname)
        .join(";"),
      ForwardedHeaders: {
        KnownProxies: ["10.0.0.10"],
        AllowedHosts: clients.map((client) => new URL(client.origin).hostname),
      },
      Cors: {
        Origins: clients.map((client) => new URL(client.origin).origin),
      },
      Logging: {
        LogLevel: {
          "Microsoft.AspNetCore.Hosting.Diagnostics": "Warning",
          "Microsoft.AspNetCore.Authentication.OpenIdConnect": "Warning",
          "Yarp.ReverseProxy.Forwarder.HttpForwarder": "Warning",
        },
      },
      Bff: {
        RedisConnectionStringKey: setup.redisKey,
        ...(!options.native && {
          ApplicationName: setup.application,
          KeyNamespace: setup.namespace,
          Clients: Object.fromEntries(
            clients.map((client) => [
              client.name,
              {
                PublicOrigin: new URL(client.origin).origin,
                PathBase: new URL(
                  client.pathBase || "/",
                  client.origin,
                ).pathname.replace(/\/+$/, ""),
                ...(options.forwardCookies && {
                  ForwardCookies: true,
                }),
                OpenIdConnect: {
                  Authority: client.authority,
                  ClientId: client.clientId,
                  Scope: client.scopes.trim().split(/\s+/),
                },
              },
            ]),
          ),
        }),
      },
      ReverseProxy: {
        Routes: Object.fromEntries(
          clients.map((client) => [
            `${client.name}-api`,
            {
              ClusterId: `${client.name}-resource-api`,
              Match: {
                ...(!options.native && {
                  Hosts: [new URL(client.origin).host],
                }),
                Path: `${client.apiPath}/{**all}`,
              },
            },
          ]),
        ),
        Clusters: Object.fromEntries(
          clients.map((client) => [
            `${client.name}-resource-api`,
            {
              Destinations: { primary: { Address: client.api } },
            },
          ]),
        ),
      },
    },
    null,
    2,
  );
}

export const hostCode = `using MonixOne.OidcProxy.Yarp;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOidcProxy(builder.Configuration);

var app = builder.Build();
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

export function createCookieConfiguration(setup: Setup): string {
  const cluster = `${setup.name}-resource-api`;
  return JSON.stringify(
    {
      Bff: { Clients: { [setup.name]: { ForwardCookies: true } } },
      ReverseProxy: {
        Routes: {
          [`${setup.name}-api`]: {
            ClusterId: cluster,
            Match: {
              Hosts: [new URL(setup.origin).host],
              Path: `${setup.apiPath}/{**all}`,
            },
          },
          [`${setup.name}-google-callback`]: {
            ClusterId: cluster,
            AuthorizationPolicy: "anonymous",
            Match: {
              Hosts: [new URL(setup.origin).host],
              Path: `${setup.apiPath}/connections/google/callback`,
              Methods: ["GET"],
            },
          },
        },
      },
    },
    null,
    2,
  );
}

export const flows = {
  login: {
    title: "Вход",
    summary:
      "Браузер проходит вход у провайдера, BFF сохраняет токены в Redis.",
    steps: [
      [
        "Браузер → BFF",
        "SPA открывает /.auth/login обычной навигацией. BFF выбирает клиента по публичному origin и PathBase; framework создаёт state, nonce и PKCE.",
        "Локальный returnUrl позволяет вернуться на нужную страницу SPA.",
      ],
      [
        "BFF → OpenID Provider",
        "Браузер переходит на authorization endpoint. Пользователь входит на стороне провайдера.",
        "Провайдеру нужны зарегистрированный confidential client и точный callback URI.",
      ],
      [
        "Provider → BFF callback",
        "Callback получает code/state. Framework проверяет correlation, а BFF обменивает code с client secret и PKCE verifier.",
        "Неопределённый исход обмена не повторяется.",
      ],
      [
        "BFF → Redis → браузер",
        "После проверки ID token BFF записывает зашифрованную сессию в Redis. Браузер получает cookie со случайной ссылкой и возвращается в SPA.",
        "Cookie: Secure, HttpOnly, SameSite=Strict. OAuth-токены остаются на сервере.",
      ],
      [
        "React → /.auth/me",
        "SPA получает claims через same-origin fetch с X-CSRF: 1. Без сессии возвращается 401.",
        "Claims нужны UI; права на ресурсы проверяет API.",
      ],
    ],
  },
  api: {
    title: "Запрос API",
    summary: "SPA отправляет cookie, внутренний API получает серверный Bearer.",
    steps: [
      [
        "SPA → /api/…",
        "Для защищённых API SPA использует credentials: same-origin и X-CSRF: 1, включая GET.",
        "Чужой Origin или Sec-Fetch-Site отклоняется.",
      ],
      [
        "BFF → Redis",
        "BFF проверяет свою cookie и серверную сессию. При необходимости выполняется refresh.",
        "Нет сессии — 401; Redis недоступен — 503.",
      ],
      [
        "YARP → Resource API",
        "YARP удаляет browser Cookie/Authorization и добавляет access token из серверной сессии. Destination берётся из конфигурации.",
        "Внутренний HTTP работает без флагов; HTTPS проверяет сертификат.",
      ],
      [
        "Resource API → SPA",
        "API проверяет issuer, audience, scopes и роли. BFF возвращает ответ без токенов; Set-Cookie по умолчанию удаляется.",
        "Неуспешная бизнес-операция автоматически не повторяется.",
      ],
    ],
  },
  refresh: {
    title: "Продление",
    summary:
      "Refresh выполняет сервер. Параллельные запросы используют одного владельца обмена.",
    steps: [
      [
        "Активная SPA → BFF",
        "Sample проверяет /.auth/me раз в 60 секунд в видимой авторизованной вкладке и обновляет данные после focus/reconnect.",
        "Polling продлевает idle даже без кликов, но не absolute lifetime.",
      ],
      [
        "Один владелец refresh",
        "Внутри реплики запросы ждут общую задачу. Между репликами владение определяется Redis CAS.",
        "Отмена одного ожидающего не отменяет остальных.",
      ],
      [
        "BFF → token endpoint",
        "Владелец отправляет refresh grant. После timeout или другого неопределённого исхода обмен не повторяется.",
        "Серверный бюджет — 30 секунд; остановка хоста его отменяет.",
      ],
      [
        "BFF → Redis, CAS",
        "Новые токены сохраняются только для той же незавершённой операции в пределах её срока.",
        "Logout удалил запись — поздний refresh её не восстанавливает.",
      ],
      [
        "Ошибка → повторный вход",
        "Некорректный или неопределённый результат закрывает сессию. При неизвестном исходе инициатор получает 503, следующий запрос — 401.",
        "Redis и IdP не образуют общую транзакцию.",
      ],
    ],
  },
  connections: {
    title: "Подключения",
    summary:
      "ForwardCookies включает изолированный cookie bridge приложения; точный callback объявляется обычным YARP route.",
    steps: [
      [
        "SPA → protected start",
        "SPA вызывает start с BFF cookie и X-CSRF. Backend получает серверный Bearer и создаёт OAuth-транзакцию.",
        "Bearer передаётся только защищённым маршрутам подключённой SPA.",
      ],
      [
        "Backend → namespaced cookie",
        "BFF выдаёт __Host-Http-cookie с Path=/, без Domain, с Secure/HttpOnly. Исходный backend Path проверяется сервером, expiry сохраняется. Отдельная CookiePolicy не требуется.",
        "Браузер запрещает Domain-cookie с этим префиксом: соседний поддомен не может подложить correlation cookie.",
      ],
      [
        "Browser → внешний provider",
        "Браузер открывает URL внешнего провайдера. Техническая HttpOnly cookie хранится автоматически.",
        "Для GET callback подходит Lax; cross-site POST требует явного SameSite=None.",
      ],
      [
        "Provider → anonymous callback",
        "На точном callback BFF восстанавливает разрешённое имя cookie. BFF session и access token не передаются.",
        "Сам backend проверяет state, связь с cookie, срок и однократность операции.",
      ],
      [
        "Backend → clearCookie",
        "Очистка использует тот же namespace и backend scope, удаляя browser cookie с Path=/.",
        "Параллельные попытки одного подключения должен поддерживать backend.",
      ],
    ],
  },
  logout: {
    title: "Выход",
    summary:
      "Сначала удаляется локальная сессия, затем выполняется выход у провайдера.",
    steps: [
      [
        "React → antiforgery endpoint",
        "SPA отменяет запросы, получает /.auth/antiforgery с X-CSRF и отправляет native POST form на /.auth/end-session.",
        "Antiforgery/logout не ждут refresh.",
      ],
      [
        "BFF → Redis GETDEL",
        "BFF удаляет сессию и забирает токены, актуальные на момент удаления. Затем очищается cookie клиента.",
        "Локальные сессии других клиентов остаются изолированными.",
      ],
      [
        "BFF → revocation endpoint",
        "BFF пытается отозвать текущий refresh token с ограниченным таймаутом.",
        "Отзыв best effort: недоступность IdP не возвращает удалённую сессию.",
      ],
      [
        "Браузер → Provider → callback",
        "Браузер проходит RP-initiated logout и возвращается на зарегистрированный signout callback.",
        "Глобальную SSO-политику logout определяет IdP.",
      ],
      [
        "Другие вкладки",
        "Следующий запрос со старой сессией получает 401. UI очищает пользовательский кэш и предлагает вход.",
        "Logout не отменяет уже отправленные API-запросы.",
      ],
    ],
  },
} as const;
