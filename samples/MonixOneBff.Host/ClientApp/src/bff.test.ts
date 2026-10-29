import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BffError, bffGet, readSession, submitLogout } from "./bff";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
afterEach(() => vi.unstubAllGlobals());

describe("BFF browser contract", () => {
  it("sends same-origin credentials and the CSRF header without bearer tokens", async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({ value: 1 })),
    );
    await expect(bffGet("/demo-api/status")).resolves.toEqual({ value: 1 });
    expect(fetch).toHaveBeenCalledWith("/demo-api/status", {
      credentials: "same-origin",
      headers: { "X-CSRF": "1", Accept: "application/json" },
    });
  });

  it("treats 401 as an ended session", async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 401 }));
    await expect(readSession()).resolves.toBeNull();
  });

  it("preserves service failures rather than presenting them as a logged-out session", async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 503 }));
    await expect(readSession()).rejects.toMatchObject({ status: 503 });
  });

  it("rejects malformed claim contracts", async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({ role: "unexpected-string" })),
    );
    await expect(readSession()).rejects.toThrow("некорректные данные");
  });

  it("forwards cancellation to fetch", async () => {
    const controller = new AbortController();
    vi.mocked(fetch).mockResolvedValue(
      new Response(JSON.stringify({ sub: ["user-1"] })),
    );
    await readSession(controller.signal);
    expect(vi.mocked(fetch).mock.calls[0]?.[1]?.signal).toBe(controller.signal);
  });

  it("performs native form navigation with the framework antiforgery token", async () => {
    vi.mocked(fetch).mockResolvedValue(
      new Response(
        JSON.stringify({
          requestToken: "csrf-token",
          formFieldName: "__RequestVerificationToken",
        }),
      ),
    );
    const submit = vi
      .spyOn(HTMLFormElement.prototype, "submit")
      .mockImplementation(function (this: HTMLFormElement) {
        expect(this.method).toBe("post");
        expect(new URL(this.action).pathname).toBe("/.auth/end-session");
        expect(new FormData(this).get("__RequestVerificationToken")).toBe(
          "csrf-token",
        );
      });
    await submitLogout();
    expect(submit).toHaveBeenCalledOnce();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe("/.auth/antiforgery");
    expect(document.querySelector("form")).toBeNull();
  });

  it("does not submit a logout form when token acquisition fails", async () => {
    vi.mocked(fetch).mockResolvedValue(new Response(null, { status: 503 }));
    const submit = vi
      .spyOn(HTMLFormElement.prototype, "submit")
      .mockImplementation(() => {});
    await expect(submitLogout()).rejects.toBeInstanceOf(BffError);
    expect(submit).not.toHaveBeenCalled();
  });

  it("rejects a malformed antiforgery response", async () => {
    vi.mocked(fetch).mockResolvedValue(new Response("{}"));
    await expect(submitLogout()).rejects.toThrow("токен защиты");
  });
});
