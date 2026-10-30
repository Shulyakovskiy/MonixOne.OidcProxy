import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/react";
import Documentation from "./Documentation";
import {
  createConfiguration,
  createCookieConfiguration,
  initialSetup,
  validateClients,
  validateSetup,
} from "./documentationContent";
import {
  inspectRoute,
  presets,
  type RouteScenario,
} from "./documentationModel";

beforeEach(() => window.history.replaceState(null, "", "/docs"));
afterEach(() => vi.unstubAllGlobals());
const configRegion = () =>
  screen.getByRole("region", { name: "appsettings.json — без секретов" });
const scenario: RouteScenario = {
  profile: true,
  route: "bff-api",
  session: true,
  csrf: true,
  foreign: false,
  bearer: true,
  scope: true,
  redis: true,
};

it("generates one native YARP configuration, without duplicate BFF bindings or secrets", () => {
  const setup = {
    ...initialSetup,
    clientId: 'portal"quoted',
    namespace: "frontend",
  };
  const config = JSON.parse(createConfiguration(setup));
  expect(validateSetup(setup)).toEqual([]);
  expect(config.Bff.Clients.spa.OpenIdConnect.ClientId).toBe(setup.clientId);
  expect(config.Bff.Clients.spa.OpenIdConnect.ClientSecret).toBeUndefined();
  expect(config.Bff.EncryptionKey).toBeUndefined();
  expect(config.ConnectionStrings).toBeUndefined();
  expect(config.Bff.KeyNamespace).toBe("frontend");
  expect(config.Bff.RedisConnectionStringKey).toBe(
    "RateLimiting:RedisConnectionString",
  );
  expect(config.Bff.Clients.spa.Routes).toBeUndefined();
  expect(config.ReverseProxy.Routes["spa-api"].Match).toEqual({
    Hosts: ["spa.example.com"],
    Path: "/api/{**all}",
  });
  expect(
    config.ReverseProxy.Clusters["spa-resource-api"].Destinations.primary
      .Address,
  ).toBe(setup.api);
});

it.each(["origin", "authority"] as const)(
  "rejects public HTTP for %s",
  (field) => {
    expect(
      validateSetup({
        ...initialSetup,
        [field]: "http://public.example",
      }).some((error) => error.includes("HTTPS URL")),
    ).toBe(true);
  },
);

it.each(["/api/", "/api//users", "api", "/api?secret", "/../api"])(
  "rejects unsafe API path %s",
  (apiPath) => {
    expect(validateSetup({ ...initialSetup, apiPath }).length).toBeGreaterThan(
      0,
    );
  },
);

it("hides generated configuration for unsafe URLs", () => {
  render(<Documentation />);
  fireEvent.change(screen.getByLabelText("SPA / BFF origin"), {
    target: { value: "https://user:password@spa.example/path" },
  });
  expect(screen.getByRole("alert")).toHaveTextContent("без credentials");
  expect(
    screen.queryByRole("region", {
      name: "appsettings.json — без секретов",
    }),
  ).toBeNull();
});

it("updates mounted callback URIs and SPA code without requests or storage", () => {
  const fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  render(<Documentation />);
  fireEvent.change(screen.getByLabelText("SPA / BFF origin"), {
    target: { value: "https://portal.example:8443" },
  });
  fireEvent.change(screen.getByLabelText("PathBase"), {
    target: { value: "/workspace/portal" },
  });
  expect(
    within(
      screen.getByRole("region", { name: "Redirect URI · spa" }),
    ).getByText(
      "https://portal.example:8443/workspace/portal/.auth/login/callback",
    ),
  ).toBeVisible();
  expect(
    within(
      screen.getByRole("region", {
        name: "Post logout redirect URI · spa",
      }),
    ).getByText(
      "https://portal.example:8443/workspace/portal/.auth/signout-callback",
    ),
  ).toBeVisible();
  fireEvent.click(screen.getByRole("button", { name: "SPA-клиент" }));
  expect(
    screen.getByRole("region", { name: "Login и чтение сессии" }),
  ).toHaveTextContent("/workspace/portal/.auth/me");
  expect(fetchMock).not.toHaveBeenCalled();
  expect(localStorage.length).toBe(0);
  expect(sessionStorage.length).toBe(0);
});

it("selects Local and Dev presets with distinct public callbacks", () => {
  render(<Documentation />);
  fireEvent.click(screen.getByRole("button", { name: "Admin · Local" }));
  expect(configRegion()).toHaveTextContent('"PathBase": "/admin"');
  expect(configRegion()).toHaveTextContent('"Path": "/calendar-api/{**all}"');
  expect(
    screen.getByRole("region", { name: "Redirect URI · admin-identity" }),
  ).toHaveTextContent("https://localhost:8443/admin/.auth/login/callback");
  fireEvent.click(screen.getByRole("button", { name: "Admin · Dev" }));
  expect(
    screen.getByRole("region", { name: "Redirect URI · admin-identity" }),
  ).toHaveTextContent(
    "https://admin.id-dev.intra-beetroute.ru/admin/.auth/login/callback",
  );
  expect(configRegion()).not.toHaveTextContent("localhost");
});

it("supports native GW without SPA settings or encryption/client secrets", () => {
  render(<Documentation />);
  fireEvent.change(screen.getByLabelText("OIDC Authority"), {
    target: { value: "" },
  });
  fireEvent.click(
    screen.getByRole("checkbox", { name: "Без SPA / native GW" }),
  );
  const config = JSON.parse(configRegion().querySelector("code")!.textContent!);
  expect(config.Bff.Clients).toBeUndefined();
  expect(config.ReverseProxy.Routes["spa-api"].Match.Hosts).toBeUndefined();
  expect(
    screen.queryByRole("heading", { name: "3. Регистрация в Identity" }),
  ).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Секреты" }));
  const secrets = screen.getByRole("region", {
    name: "Environment / secret provider",
  });
  expect(secrets).toHaveTextContent("RateLimiting__RedisConnectionString");
  expect(secrets).not.toHaveTextContent("EncryptionKey");
  expect(secrets).not.toHaveTextContent("ClientSecret");
});

it("generates independent clients with exact host/port bindings", () => {
  const second = {
    ...initialSetup,
    name: "workspace",
    origin: "https://workspace.example:8443",
    pathBase: "/app",
  };
  const config = JSON.parse(createConfiguration(initialSetup, [second]));
  expect(validateClients(initialSetup, [second])).toEqual([]);
  expect(config.ReverseProxy.Routes["workspace-api"].Match.Hosts).toEqual([
    "workspace.example:8443",
  ]);
  expect(config.Bff.Clients.workspace.PathBase).toBe("/app");
  expect(config.Bff.Clients.workspace.Routes).toBeUndefined();
  expect(config.ForwardedHeaders.AllowedHosts).toEqual([
    "spa.example.com",
    "workspace.example",
  ]);
});

it("rejects duplicate effective origins and names", () => {
  const errors = validateClients(initialSetup, [
    { ...initialSetup, origin: "https://spa.example.com:443" },
  ]);
  expect(errors).toContain("Client names должны быть уникальны.");
  expect(errors.some((error) => error.includes("отдельный origin"))).toBe(true);
});

it("adds and removes clients without reusing an existing default name", () => {
  render(<Documentation />);
  const add = screen.getByRole("button", { name: /Добавить SPA/ });
  fireEvent.click(add);
  fireEvent.click(add);
  fireEvent.click(screen.getByRole("button", { name: "Удалить SPA 2" }));
  fireEvent.click(add);
  const config = JSON.parse(configRegion().querySelector("code")!.textContent!);
  expect(Object.keys(config.Bff.Clients)).toEqual(["spa", "spa3", "spa2"]);
  expect(screen.queryByRole("alert")).toBeNull();
});

it("generates a cookie bridge with only the exact GET callback anonymous", () => {
  const setup = presets.local!.setup;
  const config = JSON.parse(createCookieConfiguration(setup));
  expect(config.Bff.Clients[setup.name]).toEqual({ ForwardCookies: true });
  const api = config.ReverseProxy.Routes[`${setup.name}-api`];
  const callback = config.ReverseProxy.Routes[`${setup.name}-google-callback`];
  expect(api.AuthorizationPolicy).toBeUndefined();
  expect(callback.AuthorizationPolicy).toBe("anonymous");
  expect(callback.Match).toEqual({
    Hosts: ["localhost:8443"],
    Path: "/calendar-api/connections/google/callback",
    Methods: ["GET"],
  });
  expect(callback.ClusterId).toBe(api.ClusterId);
});

it("navigates diagrams, refresh and logout races", () => {
  render(<Documentation />);
  fireEvent.click(screen.getByRole("link", { name: /Как работает/ }));
  fireEvent.click(screen.getByRole("button", { name: "Продление" }));
  fireEvent.click(screen.getByRole("button", { name: /4.*BFF → Redis, CAS/ }));
  expect(screen.getByText(/Logout удалил запись/)).toBeVisible();
  expect(screen.getByRole("img", { name: /GW \/ YARP → Redis/ })).toBeVisible();
  fireEvent.click(screen.getByRole("button", { name: "Выход" }));
  fireEvent.click(
    screen.getByRole("button", { name: /2.*BFF → Redis GETDEL/ }),
  );
  expect(screen.getByText(/на момент удаления/)).toBeVisible();
  const architecture = screen.getByRole("region", {
    name: "Интерактивная схема архитектуры",
  });
  fireEvent.click(within(architecture).getByRole("button", { name: /Redis/ }));
  fireEvent.click(screen.getByRole("checkbox", { name: "Без SPA" }));
  expect(
    within(architecture).queryByRole("button", { name: /Redis/ }),
  ).toBeNull();
  expect(architecture).toHaveTextContent("Профили SPA отсутствуют");
});

it("opens deep links and handles browser navigation", () => {
  window.history.replaceState(null, "", "/docs#sessions");
  render(<Documentation />);
  expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
    "Сессии и cookies",
  );
  window.history.replaceState(null, "", "/docs#checks");
  fireEvent(window, new HashChangeEvent("hashchange"));
  expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
    "Проверка и ошибки",
  );
});

it("updates the route inspector when CSRF and profile conditions change", () => {
  render(<Documentation />);
  fireEvent.click(screen.getByRole("link", { name: /Маршруты/ }));
  expect(screen.getByRole("status")).toHaveTextContent("YARP → сервис");
  fireEvent.click(screen.getByRole("checkbox", { name: "X-CSRF: 1" }));
  expect(screen.getByRole("status")).toHaveTextContent("403");
  fireEvent.click(
    screen.getByRole("checkbox", { name: "Профиль SPA подключён" }),
  );
  expect(screen.getByRole("status")).toHaveTextContent("Native YARP");
});

it.each([
  [{ session: false }, "401"],
  [{ csrf: false }, "403"],
  [{ foreign: true }, "403"],
  [{ redis: false, csrf: false }, "503"],
  [{ route: "native-api", session: false }, "YARP → сервис"],
  [{ route: "native-api", bearer: false }, "401"],
  [{ route: "native-api", scope: false }, "403"],
  [{ profile: false, route: "auth" }, "404"],
  [{ profile: false }, "Native YARP"],
  [
    {
      route: "callback",
      session: false,
      csrf: false,
      foreign: true,
      redis: false,
    },
    "YARP → callback",
  ],
  [{ route: "spa", session: false, csrf: false, redis: false }, "YARP → SPA"],
  [{ route: "auth" }, "Claims сессии"],
] satisfies [Partial<RouteScenario>, string][])(
  "models route boundary %j → %s",
  (changes, result) => {
    expect(inspectRoute({ ...scenario, ...changes }).result).toBe(result);
  },
);
