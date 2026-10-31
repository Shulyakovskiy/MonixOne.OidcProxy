import { initialSetup, type Setup } from "./documentationContent";

export const presets: Record<string, { title: string; setup: Setup }> = {
  generic: { title: "Своя SPA", setup: initialSetup },
  local: {
    title: "Admin · Local",
    setup: {
      ...initialSetup,
      name: "admin-identity",
      origin: "https://localhost:8443",
      pathBase: "/admin",
      authority: "https://localhost:8444/",
      clientId: "br-admin-identity-bff",
      api: "http://127.0.0.1:8000/",
      apiPath: "/calendar-api",
      environment: "Local",
      scopes:
        "openid offline_access profile-service.full_access location-service.full_access storage-service.full_access",
    },
  },
  dev: {
    title: "Admin · Dev",
    setup: {
      ...initialSetup,
      name: "admin-identity",
      origin: "https://admin.id-dev.intra-beetroute.ru",
      pathBase: "/admin",
      authority: "https://id-dev.intra-beetroute.ru/",
      clientId: "br-admin-identity-bff",
      api: "http://stack-srv-admin-identity-dev_admin-identity:8000/",
      apiPath: "/calendar-api",
      scopes:
        "openid offline_access profile-service.full_access location-service.full_access storage-service.full_access",
    },
  },
};

export const gatewayHostCode = `using MonixOne.OidcProxy.Yarp;

// После штатной регистрации JWT/scope, rate limiting и observability GW:
services.AddOidcProxy(configuration);

// После builder.Build():
app.UseOidcProxy(pipeline =>
{
    pipeline.UseOpenObservability();
    pipeline.UseCors(OidcProxyDefaults.CorsPolicy);
    pipeline.UseRateLimiter();
});`;

export const pipeline = [
  [
    "Forwarded headers",
    "Восстановить For / Proto / Host до выбора клиента. По умолчанию проверяются KnownProxies/KnownIPNetworks; для закрытого GW доступен явный TrustAllProxies с одним hop. AllowedHosts сохраняется.",
  ],
  [
    "PathBase",
    "Снять префикс только на HTTPS host подключённой SPA. Публичные callback, assets и cookie paths сохраняют PathBase.",
  ],
  [
    "Routing",
    "Выбрать endpoint и cluster. Metadata уже доступны observability и rate limiting.",
  ],
  [
    "Host middleware",
    "Callback GW: observability → CORS → rate limiter. CORS policy регистрирует пакет; host сохраняет порядок middleware.",
  ],
  [
    "Authentication",
    "Native JWT и именованные OIDC/cookie schemes работают вместе. Чужой host не может принять callback SPA.",
  ],
  [
    "BFF / CSRF",
    "Для подключённых SPA проверить cookie, Origin/Sec-Fetch-Site, X-CSRF и сессию. Native-маршруты сохраняют своё поведение.",
  ],
  [
    "Authorization",
    "Применить YARP policies. Native scopes и бизнес-права API проверяются независимо.",
  ],
  [
    "Endpoints / YARP",
    "BFF endpoints и MapReverseProxy подключаются один раз. Destinations и балансировка остаются в ReverseProxy.",
  ],
] as const;

export type Actor =
  "browser" | "edge" | "gateway" | "identity" | "redis" | "api";
export const actors: Record<
  Actor,
  { title: string; subtitle: string; detail: string }
> = {
  browser: {
    title: "Браузер",
    subtitle: "SPA · HttpOnly cookie",
    detail:
      "Отправляет cookie и X-CSRF: 1 на тот же origin. Access/refresh tokens и client secret не получает.",
  },
  edge: {
    title: "Балансировщик",
    subtitle: "Публичный HTTPS",
    detail:
      "Сохраняет Host и PathBase, перезаписывает X-Forwarded-For/Proto/Host. Доступ к внутреннему HTTP ограничен доверенной сетью.",
  },
  gateway: {
    title: "GW / YARP",
    subtitle: "BFF · маршруты · policies",
    detail:
      "Пакет выбирает SPA по HTTPS host/port, хранит токены на сервере и подставляет Bearer только её API. Без PublicOrigin профиля остаётся native YARP.",
  },
  identity: {
    title: "Identity / IdP",
    subtitle: "Вход · Code / PKCE",
    detail:
      "Confidential client, точные callbacks, Code + PKCE S256, refresh и logout. Провайдер отвечает за browser login и глобальную SSO-политику.",
  },
  redis: {
    title: "Redis",
    subtitle: "Data Protection · key ring",
    detail:
      "Общий key ring и отдельные session keys клиентов. Нет memory fallback. Реплики используют одинаковые область ключей и EncryptionKey.",
  },
  api: {
    title: "Сервис",
    subtitle: "JWT · scopes · роли",
    detail:
      "Проверяет issuer, audience, подпись и бизнес-права. Для Calendar внешние OAuth credentials, state/nonce и одноразовость остаются ответственностью backend.",
  },
};

export const flowHops: Record<string, readonly (readonly [Actor, Actor])[]> = {
  login: [
    ["browser", "gateway"],
    ["browser", "identity"],
    ["browser", "gateway"],
    ["gateway", "redis"],
    ["browser", "gateway"],
  ],
  api: [
    ["browser", "gateway"],
    ["gateway", "redis"],
    ["gateway", "api"],
    ["api", "browser"],
  ],
  refresh: [
    ["browser", "gateway"],
    ["gateway", "redis"],
    ["gateway", "identity"],
    ["gateway", "redis"],
    ["gateway", "browser"],
  ],
  connections: [
    ["browser", "gateway"],
    ["api", "browser"],
    ["browser", "identity"],
    ["browser", "gateway"],
    ["api", "browser"],
  ],
  logout: [
    ["browser", "gateway"],
    ["gateway", "redis"],
    ["gateway", "identity"],
    ["browser", "identity"],
    ["gateway", "browser"],
  ],
};

export type RouteScenario = {
  profile: boolean;
  route: "bff-api" | "native-api" | "spa" | "callback" | "auth";
  session: boolean;
  csrf: boolean;
  foreign: boolean;
  bearer: boolean;
  scope: boolean;
  redis: boolean;
};
export function inspectRoute(s: RouteScenario): {
  result: string;
  detail: string;
} {
  if (!s.profile && s.route === "auth")
    return {
      result: "404",
      detail:
        "BFF endpoints не зарегистрированы: PublicOrigin профиля отсутствует.",
    };
  if (s.route === "spa")
    return {
      result: "YARP → SPA",
      detail:
        "Anonymous SPA route: assets доступны без сессии. Серверный Bearer не передаётся.",
    };
  if (s.route === "callback")
    return {
      result: "YARP → callback",
      detail:
        "Точный GET anonymous callback не требует BFF-сессии. Backend обязан проверить state/cookie, TTL и однократность.",
    };
  if (!s.profile && s.route === "bff-api")
    return {
      result: "Native YARP",
      detail:
        "Профиль отключён. Действуют только native AuthorizationPolicy маршрута и проверки backend. JWT/scope-защита не добавляется автоматически.",
    };
  if (s.route === "native-api") {
    if (!s.bearer)
      return {
        result: "401",
        detail:
          "Защищённая native policy требует Bearer. Cookie BFF его не заменяет.",
      };
    if (!s.scope)
      return {
        result: "403",
        detail: "JWT принят, но native scope policy отказала в доступе.",
      };
    return {
      result: "YARP → сервис",
      detail:
        "Исходный Bearer сохраняется. Backend самостоятельно проверяет права и формирует ответ.",
    };
  }
  if (s.foreign)
    return {
      result: "403",
      detail: "Чужой Origin или Sec-Fetch-Site отклоняется до backend.",
    };
  if (!s.redis && s.session)
    return {
      result: "503",
      detail:
        "Cookie ссылается на серверную сессию, но Redis недоступен. Memory fallback нет.",
    };
  if (!s.csrf)
    return {
      result: "403",
      detail: "На защищённом BFF API нужен X-CSRF: 1, включая GET.",
    };
  if (!s.session)
    return {
      result: "401",
      detail:
        "Нет действующей BFF-сессии. Валидный native JWT не заменяет cookie-сессию.",
    };
  return s.route === "auth"
    ? {
        result: "Claims сессии",
        detail:
          "BFF endpoint возвращает claims, без OAuth-токенов. Запрос в backend не выполняется.",
      }
    : {
        result: "YARP → сервис",
        detail:
          "GW получает access token из сессии и подставляет Bearer. Это не гарантия успешного ответа backend.",
      };
}
