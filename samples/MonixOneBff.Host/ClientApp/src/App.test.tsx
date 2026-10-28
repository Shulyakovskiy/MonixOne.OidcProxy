import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import App from "./App";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
afterEach(() => vi.unstubAllGlobals());

it("offers top-level Identity login when no session exists", async () => {
  vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 401 }));
  render(<App />);
  expect(
    await screen.findByRole("link", { name: /Войти через Identity/ }),
  ).toHaveAttribute("href", "/.auth/login");
});

it("displays an unavailable backend and can retry", async () => {
  vi.mocked(fetch)
    .mockResolvedValueOnce(new Response(null, { status: 503 }))
    .mockResolvedValueOnce(
      new Response(
        JSON.stringify({ sub: ["user-1"], name: ["Anna"], role: ["manager"] }),
      ),
    );
  render(<App />);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "временно недоступен",
  );
  fireEvent.click(screen.getByRole("button", { name: "Повторить" }));
  expect(await screen.findByText("Anna")).toBeVisible();
});

it("returns to login when the session expires during an API request", async () => {
  vi.mocked(fetch)
    .mockResolvedValueOnce(
      new Response(JSON.stringify({ sub: ["user-1"], name: ["Anna"] })),
    )
    .mockResolvedValueOnce(new Response(null, { status: 401 }));
  render(<App />);
  fireEvent.click(await screen.findByRole("button", { name: "Проверить API" }));
  expect(
    await screen.findByRole("link", { name: /Войти через Identity/ }),
  ).toBeVisible();
  expect(screen.getByRole("alert")).toHaveTextContent("Сессия завершена");
});

it("renders the protected API response without placing credentials in browser storage", async () => {
  vi.mocked(fetch)
    .mockResolvedValueOnce(new Response(JSON.stringify({ sub: ["user-1"] })))
    .mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          service: "MonixOneBff.Host",
          subject: "user-1",
          environment: "Testing",
          utc: "2026-10-28T12:00:00Z",
        }),
      ),
    );
  render(<App />);
  fireEvent.click(await screen.findByRole("button", { name: "Проверить API" }));
  expect(
    await screen.findByText(/"service": "MonixOneBff.Host"/),
  ).toBeVisible();
  expect(localStorage.length).toBe(0);
  expect(sessionStorage.length).toBe(0);
});
