import { afterEach, expect, it, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/react";
import Documentation from "./Documentation";
import { createConfiguration, initialSetup, validateSetup } from "./documentationContent";

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
  expect(configuration.OpenIdConnect.ClientId).toBe(setup.clientId);
  expect(configuration.OpenIdConnect.ClientSecret).toBeUndefined();
  expect(configuration.ConnectionStrings).toBeUndefined();
  expect(configuration.Bff.KeyNamespace).toBe("frontend");
  expect(configuration.ReverseProxy.Clusters["resource-api"].Destinations.primary.Address).toBe(setup.api);
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
