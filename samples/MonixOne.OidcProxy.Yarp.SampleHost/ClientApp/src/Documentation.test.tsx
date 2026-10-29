import { afterEach, expect, it, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/react";
import Documentation from "./Documentation";
import { createConfiguration, initialSetup, validateSetup, validateClients } from "./documentationContent";

afterEach(() => vi.unstubAllGlobals());

it("rejects an unsafe public origin before generating configuration", () => {
  // Arrange
  render(<Documentation />);
  // Act
  fireEvent.change(screen.getByLabelText("SPA / BFF origin"), { target: { value: "https://user:secret@spa.example/path" } });
  // Assert
  expect(screen.getByRole("alert")).toHaveTextContent("без credentials");
  fireEvent.click(screen.getByRole("button", { name: /04.*Хост и YARP/ }));
  expect(screen.getByRole("alert")).toHaveTextContent("Сначала исправьте параметры");
  expect(screen.queryByRole("region", { name: "appsettings.json — без секретов" })).toBeNull();
});

it("generates valid server configuration without secret fields", () => {
  // Arrange
  const setup = { ...initialSetup, clientId: 'portal"quoted', namespace: "frontend" };
  // Act
  const configuration = JSON.parse(createConfiguration(setup));
  // Assert
  expect(validateSetup(setup)).toEqual([]);
  expect(configuration.Bff.Clients.spa.OpenIdConnect.ClientId).toBe(setup.clientId);
  expect(configuration.Bff.Clients.spa.OpenIdConnect.ClientSecret).toBeUndefined();
  expect(configuration.ConnectionStrings).toBeUndefined();
  expect(configuration.Bff.KeyNamespace).toBe("frontend");
  expect(configuration.ReverseProxy.Clusters["spa-resource-api"].Destinations.primary.Address).toBe(setup.api);
});

it("updates callback URIs without network requests or browser storage", () => {
  // Arrange
  const fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  render(<Documentation />);
  // Act
  fireEvent.change(screen.getByLabelText("SPA / BFF origin"), { target: { value: "https://portal.example:8443" } });
  fireEvent.click(screen.getByRole("button", { name: /02.*OpenID Provider/ }));
  // Assert
  expect(within(screen.getByRole("region", { name: "Redirect URI" })).getByText("https://portal.example:8443/.auth/login/callback")).toBeVisible();
  expect(within(screen.getByRole("region", { name: "Post logout redirect URI" })).getByText("https://portal.example:8443/.auth/signout-callback")).toBeVisible();
  expect(fetchMock).not.toHaveBeenCalled();
  expect(localStorage.length).toBe(0);
  expect(sessionStorage.length).toBe(0);
});

it("navigates refresh and logout stages and explains the race", () => {
  // Arrange
  render(<Documentation />);
  // Act
  fireEvent.click(screen.getByRole("button", { name: /02 · Как работает SPA/ }));
  fireEvent.click(screen.getByRole("button", { name: "Продление" }));
  fireEvent.click(screen.getByRole("button", { name: /4.*BFF → Redis, CAS/ }));
  // Assert
  expect(screen.getByText(/Logout удалил запись/)).toBeVisible();
  // Act
  fireEvent.click(screen.getByRole("button", { name: "Выход" }));
  fireEvent.click(screen.getByRole("button", { name: /2.*BFF → Redis GETDEL/ }));
  // Assert
  expect(screen.getByText(/на момент удаления/)).toBeVisible();
});

it("generates a mounted SPA configuration and matching callback URIs", () => {
  // Arrange
  render(<Documentation />);
  // Act
  fireEvent.change(screen.getByLabelText("PathBase (пусто для корня)"), { target: { value: "/workspace/portal" } });
  fireEvent.click(screen.getByRole("button", { name: /02.*OpenID Provider/ }));
  // Assert
  expect(within(screen.getByRole("region", { name: "Redirect URI" })).getByText("https://spa.example.com/workspace/portal/.auth/login/callback")).toBeVisible();
  // Act
  fireEvent.click(screen.getByRole("button", { name: /04.*Хост и YARP/ }));
  // Assert
  expect(screen.getByRole("region", { name: "appsettings.json — без секретов" })).toHaveTextContent('"PathBase": "/workspace/portal"');
  // Act
  fireEvent.click(screen.getByRole("button", { name: /05.*React SPA/ }));
  // Assert
  expect(screen.getByRole("region", { name: "Login и чтение сессии" })).toHaveTextContent('/workspace/portal/.auth/me');
});

it("generates independent clients and exact YARP host bindings", () => {
  // Arrange
  const second = { ...initialSetup, name: "workspace", origin: "https://workspace.example:8443", clientId: "workspace-bff", pathBase: "/app" };
  // Act
  const configuration = JSON.parse(createConfiguration(initialSetup, [second]));
  // Assert
  expect(validateClients(initialSetup, [second])).toEqual([]);
  expect(Object.keys(configuration.Bff.Clients)).toEqual(["spa", "workspace"]);
  expect(configuration.Bff.Clients.workspace.OpenIdConnect.ClientId).toBe("workspace-bff");
  expect(configuration.ReverseProxy.Routes["workspace-api"].Match.Hosts).toEqual(["workspace.example:8443"]);
  expect(configuration.ReverseProxy.Routes["workspace-api"].Metadata["OidcProxy.Client"]).toBe("workspace");
  expect(configuration.Bff.Clients.workspace.PathBase).toBe("/app");
});

it("rejects duplicate effective origins and client names", () => {
  // Arrange
  const second = { ...initialSetup, origin: "https://spa.example.com:443" };
  // Act
  const errors = validateClients(initialSetup, [second]);
  // Assert
  expect(errors).toContain("Client names должны быть уникальны.");
  expect(errors.some(error => error.includes("отдельный origin"))).toBe(true);
});

it("adds a second SPA and shows its own callback URI", () => {
  // Arrange
  render(<Documentation />);
  // Act
  fireEvent.click(screen.getByRole("button", { name: "Добавить SPA" }));
  fireEvent.change(screen.getByLabelText("SPA 2 · SPA / BFF origin"), { target: { value: "https://workspace.example" } });
  fireEvent.click(screen.getByRole("button", { name: /02.*OpenID Provider/ }));
  // Assert
  expect(within(screen.getByRole("region", { name: "Redirect URI · spa2" })).getByText("https://workspace.example/.auth/login/callback")).toBeVisible();
});
