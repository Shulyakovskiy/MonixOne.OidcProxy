// @vitest-environment node
import { readFileSync } from "node:fs";
import { JSDOM, VirtualConsole } from "jsdom";
import { expect, it, vi } from "vitest";

it("opens the generated HTML offline with working navigation and no external assets", async () => {
  const html = readFileSync(
    new URL("../../../../docs/index.html", import.meta.url),
    "utf8",
  );
  const errors = [];
  const console = new VirtualConsole();
  console.on("jsdomError", (error) => errors.push(error));
  const fetch = vi.fn(() =>
    Promise.reject(new Error("Offline documentation attempted a request")),
  );
  const dom = new JSDOM(html, {
    url: "file:///docs/index.html",
    runScripts: "dangerously",
    pretendToBeVisual: true,
    virtualConsole: console,
    beforeParse(window) {
      window.fetch = fetch;
    },
  });
  try {
    const document = dom.window.document;
    await vi.waitFor(() =>
      expect(document.querySelector("h1")?.textContent).toBe("Подключение"),
    );
    expect(
      document.querySelectorAll("script[src], link[href], img[src]"),
    ).toHaveLength(0);
    expect(document.querySelector("style")?.textContent).toContain(".docs");
    expect(
      dom.window.getComputedStyle(document.querySelector(".doc-header"))
        .display,
    ).toBe("flex");
    expect(
      document
        .querySelector("meta[http-equiv='Content-Security-Policy']")
        ?.getAttribute("content"),
    ).toContain("connect-src 'none'");
    const navigation = document.querySelector("nav a[href='#flow']");
    navigation.click();
    await vi.waitFor(() =>
      expect(document.querySelector("h1")?.textContent).toBe("Как работает"),
    );
    expect(document.querySelector("svg[role=img]")).not.toBeNull();
    expect(fetch).not.toHaveBeenCalled();
    expect(errors).toEqual([]);
  } finally {
    dom.window.close();
  }
});
