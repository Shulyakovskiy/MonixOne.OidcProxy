import { useEffect, useState } from "react";
import {
  BffError,
  bffGet,
  readSession,
  submitLogout,
  type ApiStatus,
  type Claims,
} from "./bff";

type Session =
  | { status: "loading" }
  | { status: "anonymous" }
  | { status: "authenticated"; claims: Claims }
  | { status: "error" };

export default function App() {
  const [session, setSession] = useState<Session>({ status: "loading" });
  const [pending, setPending] = useState<"api" | "logout" | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [result, setResult] = useState<ApiStatus | null>(null);

  async function load(signal?: AbortSignal) {
    setSession({ status: "loading" });
    setProblem(null);
    try {
      const claims = await readSession(signal);
      if (!signal?.aborted)
        setSession(
          claims
            ? { status: "authenticated", claims }
            : { status: "anonymous" },
        );
    } catch (error) {
      if (!signal?.aborted) {
        setSession({ status: "error" });
        setProblem(
          error instanceof Error
            ? error.message
            : "Не удалось проверить сессию.",
        );
      }
    }
  }

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, []);

  async function checkApi() {
    setPending("api");
    setProblem(null);
    setResult(null);
    try {
      setResult(await bffGet<ApiStatus>("/demo-api/status"));
    } catch (error) {
      if (error instanceof BffError && error.status === 401)
        setSession({ status: "anonymous" });
      setProblem(
        error instanceof Error ? error.message : "Не удалось выполнить запрос.",
      );
    } finally {
      setPending(null);
    }
  }

  async function logout() {
    setPending("logout");
    setProblem(null);
    try {
      await submitLogout();
    } catch (error) {
      if (error instanceof BffError && error.status === 401)
        setSession({ status: "anonymous" });
      setProblem(error instanceof Error ? error.message : "Не удалось выйти.");
      setPending(null);
    }
  }

  return (
    <main className="shell">
      <header>
        <a className="brand" href="/">
          MonixOne<span>OidcProxy</span>
        </a>
        <span className="stack">React + Vite · .NET 10</span>
      </header>
      <section className="card">
        <p className="eyebrow">BFF EXAMPLE</p>
        <h1>
          Один вход.
          <br />
          <span>Защищённая сессия.</span>
        </h1>
        <p className="intro">
          Вход через Identity, проверка сессии и запрос к защищённому API.
        </p>
        <div className="status" role="status">
          <span className={`dot ${session.status}`} />
          {session.status === "loading"
            ? "Проверяем сессию…"
            : session.status === "authenticated"
              ? "Вы вошли в систему"
              : session.status === "error"
                ? "Не удалось проверить сессию"
                : "Вход не выполнен"}
        </div>
        {session.status === "authenticated" && (
          <dl>
            <div>
              <dt>Пользователь</dt>
              <dd>
                {session.claims.name?.[0] ??
                  session.claims.sub?.[0] ??
                  "Пользователь"}
              </dd>
            </div>
            <div>
              <dt>Subject</dt>
              <dd>{session.claims.sub?.[0] ?? "—"}</dd>
            </div>
            <div>
              <dt>Роли</dt>
              <dd>{session.claims.role?.join(", ") || "—"}</dd>
            </div>
          </dl>
        )}
        {problem && (
          <p className="error" role="alert">
            {problem}
          </p>
        )}
        <div className="actions">
          {session.status === "anonymous" && (
            <a className="button" href="/.auth/login">
              Войти через Identity <span aria-hidden>↗</span>
            </a>
          )}
          {session.status === "authenticated" && (
            <>
              <button
                disabled={pending !== null}
                onClick={() => void checkApi()}
              >
                {pending === "api" ? "Выполняем запрос…" : "Проверить API"}
              </button>
              <button
                className="secondary"
                disabled={pending !== null}
                onClick={() => void logout()}
              >
                {pending === "logout" ? "Выходим…" : "Выйти"}
              </button>
            </>
          )}
          {session.status === "error" && (
            <button onClick={() => void load()}>Повторить</button>
          )}
        </div>
        {result && (
          <div className="api-result">
            <span className="eyebrow">API RESPONSE</span>
            <pre>{JSON.stringify(result, null, 2)}</pre>
          </div>
        )}
      </section>
      <footer>
        MonixOne.OidcProxy{" "}
        <span>
          Сессия через HttpOnly cookie. OAuth-токены хранятся на сервере.
        </span>
      </footer>
    </main>
  );
}
