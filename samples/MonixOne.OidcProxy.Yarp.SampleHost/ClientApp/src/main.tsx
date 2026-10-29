import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import Documentation from "./Documentation";
import { routes } from "./routes";
import { QueryClientProvider } from "@tanstack/react-query";
import { createBffQueryClient } from "./session";
import "./styles.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {window.location.pathname === routes.docs ? <Documentation /> : (
      <QueryClientProvider client={createBffQueryClient()}>
        <App />
      </QueryClientProvider>
    )}
  </StrictMode>,
);
