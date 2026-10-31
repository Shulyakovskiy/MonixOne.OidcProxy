import { readFileSync } from "node:fs";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  define: {
    "import.meta.env.VITE_DOCUMENTED_APPSETTINGS": JSON.stringify(
      readFileSync(
        new URL("../../../docs/examples/br-admin-identity/appsettings.json", import.meta.url),
        "utf8",
      ),
    ),
  },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/test-setup.ts"],
    restoreMocks: true,
  },
});
