import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import react from "@vitejs/plugin-react";
import { defineConfig, type Plugin } from "vite";

const packageProject = new URL(
  "../../../src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj",
  import.meta.url,
);
const version = readFileSync(packageProject, "utf8").match(
  /<Version>([^<]+)<\/Version>/,
)?.[1];
if (!version) throw new Error("Package version is missing from the .csproj.");

// Keep the distributable offline: one generated HTML, no CDN or runtime server.
function offlineDocumentation(): Plugin {
  return {
    name: "offline-documentation",
    generateBundle: {
      order: "post",
      handler(_, bundle) {
        const chunks = Object.values(bundle).filter(
          (item) => item.type === "chunk",
        );
        if (chunks.length !== 1 || !chunks[0]?.isEntry)
          throw new Error(
            "Offline documentation must contain exactly one entry bundle.",
          );
        const script = chunks[0].code.replace(/<\/script/gi, "<\\/script");
        const css = Object.values(bundle)
          .filter(
            (item) => item.type === "asset" && item.fileName.endsWith(".css"),
          )
          .map((item) => (item.type === "asset" ? String(item.source) : ""))
          .join("\n");
        if (!css.includes(".docs"))
          throw new Error("Offline documentation stylesheet is missing.");
        for (const filename of Object.keys(bundle)) delete bundle[filename];
        this.emitFile({
          type: "asset",
          fileName: "index.html",
          source: `<!doctype html>
<html lang="ru"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'none'; base-uri 'none'; form-action 'none'">
<meta name="description" content="MonixOne.OidcProxy.Yarp: подключение, маршруты, интерактивные схемы, сессии и проверка.">
<title>MonixOne.OidcProxy.Yarp ${version} — документация</title>
<style>${css}</style></head><body><div id="root"></div><script>${script}</script></body></html>`,
        });
      },
    },
  };
}

export default defineConfig(({ command, mode }) => {
  const define = {
    "import.meta.env.VITE_PACKAGE_VERSION": JSON.stringify(version),
    "import.meta.env.VITE_DOCUMENTED_APPSETTINGS": JSON.stringify(
      readFileSync(
        new URL("../../../docs/examples/br-admin-identity/appsettings.json", import.meta.url),
        "utf8",
      ),
    ),
  };
  if (command === "build" && mode === "docs") {
    return {
      plugins: [react(), offlineDocumentation()],
      define: {
        ...define,
        "process.env.NODE_ENV": JSON.stringify("production"),
      },
      build: {
        outDir: "../../../docs",
        emptyOutDir: false,
        cssCodeSplit: false,
        lib: {
          entry: fileURLToPath(
            new URL("src/documentationMain.tsx", import.meta.url),
          ),
          name: "OidcProxyDocs",
          formats: ["iife"],
        },
      },
    };
  }
  if (command === "build") {
    return {
      plugins: [react()],
      define,
      build: { outDir: "../wwwroot", emptyOutDir: true },
    };
  }
  if (mode === "docs") {
    return {
      plugins: [react()],
      define,
      server: { host: "localhost", port: 4173, strictPort: true },
    };
  }
  const cert = process.env.DEV_HTTPS_CERT;
  const key = process.env.DEV_HTTPS_KEY;
  if (!cert || !key)
    throw new Error(
      "Set DEV_HTTPS_CERT and DEV_HTTPS_KEY to a trusted local PEM certificate and key. BFF cookies require HTTPS.",
    );
  const target = process.env.BFF_TARGET ?? "https://localhost:5001";
  if (new URL(target).protocol !== "https:")
    throw new Error("BFF_TARGET must use HTTPS.");
  const proxy = { target, secure: true, changeOrigin: false };
  return {
    plugins: [react()],
    define,
    server: {
      host: "localhost",
      port: 5173,
      strictPort: true,
      https: { cert: readFileSync(cert), key: readFileSync(key) },
      proxy: { "/.auth": proxy, "/api": proxy },
    },
  };
});
