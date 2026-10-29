import { readFileSync } from "node:fs";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

export default defineConfig(({ command }) => {
  if (command === "build") {
    return {
      plugins: [react()],
      build: { outDir: "../wwwroot", emptyOutDir: true },
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
    server: {
      host: "localhost",
      port: 5173,
      strictPort: true,
      https: { cert: readFileSync(cert), key: readFileSync(key) },
      proxy: { "/.auth": proxy, "/api": proxy, "/demo-api": proxy },
    },
  };
});
