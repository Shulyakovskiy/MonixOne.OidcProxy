import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import Documentation from "./Documentation";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Documentation />
  </StrictMode>,
);
