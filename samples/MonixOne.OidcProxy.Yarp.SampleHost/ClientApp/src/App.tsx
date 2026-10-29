import { routes } from "./routes";
import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BffError, bffGet, submitLogout, type ApiStatus, type Claims } from "./bff";
import { sessionQueryOptions, sessionQueryKey } from "./session";

type Session =
  | { status: "loading" }
  | { status: "anonymous" }
  | { status: "authenticated"; claims: Claims }
  | { status: "error" };

export default function App() {
  const loginHref = `${routes.login}?${new URLSearchParams({
    returnUrl: window.location.pathname + window.location.search + window.location.hash,
  })}`;
  const queryClient = useQueryClient();
  const [apiPending, setApiPending] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [result, setResult] = useState<ApiStatus | null>(null);
  const logoutMutation = useMutation({
    mutationFn: submitLogout,
    retry: false,
    onMutate: () => queryClient.cancelQueries(),
    onSuccess: () => { queryClient.clear(); setResult(null); },
    onError: (error) => setProblem(error.message),
  });
  const sessionQuery = useQuery({
    ...sessionQueryOptions,
    enabled: !logoutMutation.isPending && !logoutMutation.isSuccess,
  });
  const session: Session = logoutMutation.isSuccess ? { status: "anonymous" }
    : sessionQuery.isPending ? { status: "loading" }
    : sessionQuery.isError ? { status: "error" }
    : sessionQuery.data ? { status: "authenticated", claims: sessionQuery.data }
    : { status: "anonymous" };
  const pending = logoutMutation.isPending ? "logout" : apiPending ? "api" : null;
  const displayedProblem = problem ?? sessionQuery.error?.message ?? null;

  async function checkApi() {
    setApiPending(true);
    setProblem(null);
    setResult(null);
    try {
      setResult(await queryClient.fetchQuery({
        queryKey: ["protected-api", sessionQuery.data?.sub?.[0]],
        queryFn: ({ signal }) => bffGet<ApiStatus>(routes.apiStatus, signal),
        staleTime: 0, gcTime: 0, retry: false,
      }));
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") return;
      if (error instanceof BffError && error.status === 401) {
        queryClient.clear();
        queryClient.setQueryData(sessionQueryKey, null);
      }
      setProblem(error instanceof Error ? error.message : "Не удалось выполнить запрос.");
    } finally {
      setApiPending(false);
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
          Вход через OpenID Connect, проверка сессии и запрос к защищённому API.
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
        {displayedProblem && (
          <p className="error" role="alert">
            {displayedProblem}
          </p>
        )}
        <div className="actions">
          {session.status === "anonymous" && (
            <a className="button" href={loginHref}>
              Войти через OpenID Connect <span aria-hidden>↗</span>
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
                disabled={logoutMutation.isPending || logoutMutation.isSuccess}
                onClick={() => logoutMutation.mutate()}
              >
                {pending === "logout" ? "Выходим…" : "Выйти"}
              </button>
            </>
          )}
          {session.status === "error" && (
            <button onClick={() => void sessionQuery.refetch()}>Повторить</button>
          )}
        </div>
        {result && session.status === "authenticated" && (
          <div className="api-result">
            <span className="eyebrow">API RESPONSE</span>
            <pre>{JSON.stringify(result, null, 2)}</pre>
          </div>
        )}
      </section>
      <footer>
        MonixOne.OidcProxy.Yarp{" "}
        <span>
          Сессия через HttpOnly cookie. OAuth-токены хранятся на сервере.
        </span>
      </footer>
    </main>
  );
}
