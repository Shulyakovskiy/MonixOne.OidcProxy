import { routes } from "./routes";
export type Claims = Record<string, string[]>;
export type ApiStatus = {
  service: string;
  subject: string;
  environment: string;
  utc: string;
};

export class BffError extends Error {
  constructor(readonly status: number) {
    super(
      status === 401
        ? "Сессия завершена. Войдите снова."
        : status === 403
          ? "Запрос отклонён защитой BFF."
          : status === 503
            ? "Сервис временно недоступен. Повторите позже."
            : `Ошибка запроса: ${status}`,
    );
  }
}

export async function bffGet<T>(
  path: string,
  signal?: AbortSignal,
): Promise<T> {
  const response = await fetch(path, {
    credentials: "same-origin",
    headers: { "X-CSRF": "1", Accept: "application/json" },
    ...(signal ? { signal } : {}),
  });
  if (!response.ok) throw new BffError(response.status);
  return response.json() as Promise<T>;
}

export async function readSession(
  signal?: AbortSignal,
): Promise<Claims | null> {
  try {
    const claims = await bffGet<unknown>(routes.user + "?slide=false", signal);
    if (
      typeof claims !== "object" ||
      claims === null ||
      Array.isArray(claims) ||
      !Object.values(claims).every(
        (value) =>
          Array.isArray(value) &&
          value.every((item) => typeof item === "string"),
      )
    ) {
      throw new Error("BFF вернул некорректные данные сессии.");
    }
    return claims as Claims;
  } catch (error) {
    if (error instanceof BffError && error.status === 401) return null;
    throw error;
  }
}

export async function submitLogout(): Promise<void> {
  const token = await bffGet<{ requestToken: string; formFieldName: string }>(
    routes.antiforgery,
  );
  if (
    !token ||
    typeof token.requestToken !== "string" ||
    !token.requestToken ||
    typeof token.formFieldName !== "string" ||
    !token.formFieldName
  ) {
    throw new Error("BFF не выдал токен защиты выхода.");
  }
  const form = document.createElement("form");
  form.method = "POST";
  form.action = routes.logout;
  const field = document.createElement("input");
  field.type = "hidden";
  field.name = token.formFieldName;
  field.value = token.requestToken;
  form.append(field);
  document.body.append(form);
  form.submit();
  form.remove();
}
