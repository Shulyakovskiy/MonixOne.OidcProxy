export function createRoutes(pathBase: string) {
  const base = pathBase.replace(/\/+$/, "");
  return {
    docs: `${base}/docs`,
    login: `${base}/.auth/login`,
    loginCallback: `${base}/.auth/login/callback`,
    logoutCallback: `${base}/.auth/signout-callback`,
    user: `${base}/.auth/me`,
    antiforgery: `${base}/.auth/antiforgery`,
    logout: `${base}/.auth/end-session`,
    apiStatus: `${base}/api/status`,
  } as const;
}

export const routes = createRoutes(import.meta.env.BASE_URL);
