import { copyFile, mkdir } from "node:fs/promises";

const destination = new URL("../public/docs-assets/", import.meta.url);
await mkdir(destination, { recursive: true });
for (const name of ["BFF-flow.png", "BFF-login-workflow.png"]) {
  await copyFile(new URL(`../../../../${name}`, import.meta.url), new URL(name, destination));
}
await copyFile(new URL("../../../../assets/nuget-icon.png", import.meta.url), new URL("nuget-icon.png", destination));
