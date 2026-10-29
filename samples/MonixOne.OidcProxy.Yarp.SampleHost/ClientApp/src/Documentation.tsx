import { useState } from "react";
import {
  createConfiguration,
  createCookieConfiguration,
  flows,
  hostCode,
  initialSetup,
  createSpaCode,
  validateClients,
  type Setup,
  type ClientSetup,
} from "./documentationContent";
import { createRoutes, routes } from "./routes";
import "./documentation.css";

function Code({ label, value }: { label: string; value: string }) {
  const [copiedValue, setCopiedValue] = useState<string | null>(null);
  const [error, setError] = useState(false);
  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopiedValue(value);
      setError(false);
    } catch {
      setError(true);
    }
  }
  return (
    <section className="doc-code" aria-label={label}>
      <div>
        <strong>{label}</strong>
        <button type="button" onClick={copy}>
          {copiedValue === value ? "Скопировано" : "Копировать"}
        </button>
      </div>
      <pre>
        <code>{value}</code>
      </pre>
      {error && (
        <p role="status">
          Буфер обмена недоступен. Выделите код и скопируйте вручную.
        </p>
      )}
    </section>
  );
}

const steps = [
  "Топология",
  "OpenID Provider",
  "Redis и ключи",
  "Хост и YARP",
  "React SPA",
  "Проверка",
];
const fields: [keyof Setup, string][] = [
  ["name", "Client name"],
  ["origin", "SPA / BFF origin"],
  ["pathBase", "PathBase (пусто для корня)"],
  ["authority", "OIDC Authority"],
  ["clientId", "Client ID"],
  ["api", "Resource API"],
  ["application", "Application name"],
  ["environment", "Environment"],
  ["namespace", "Redis namespace"],
  ["scopes", "Scopes"],
];

export default function Documentation() {
  const [section, setSection] = useState<"setup" | "flows">("setup");
  const [step, setStep] = useState(0);
  const [setup, setSetup] = useState(initialSetup);
  const [others, setOthers] = useState<ClientSetup[]>([]);
  const [flow, setFlow] = useState<keyof typeof flows>("login");
  const [flowStep, setFlowStep] = useState(0);
  const errors = validateClients(setup, others);
  const valid = errors.length === 0;
  const targetRoutes = createRoutes(setup.pathBase);
  const current = flows[flow];
  const detail = current.steps[flowStep]!;
  const callback = valid
    ? new URL(targetRoutes.loginCallback, setup.origin).href
    : "Исправьте URL на шаге 1";
  const logoutCallback = valid
    ? new URL(targetRoutes.logoutCallback, setup.origin).href
    : "Исправьте URL на шаге 1";

  return (
    <div className="docs">
      <header className="doc-header">
        <a href={routes.docs} className="doc-brand">
          <img
            src={`${routes.docsAssets}/nuget-icon.png`}
            alt=""
            width="40"
            height="40"
          />
          MonixOne.OidcProxy.Yarp
        </a>
        <a href="https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp">
          Исходный код ↗
        </a>
      </header>
      <main>
        <div className="doc-hero">
          <p className="doc-kicker">.NET 10 · YARP · OPENID CONNECT · REDIS</p>
          <h1>
            SPA с серверной
            <br />
            <span>OAuth-сессией.</span>
          </h1>
          <p>
            Настройте BFF и разберите путь каждого запроса. В браузере — UI и
            HttpOnly cookie. Токены, refresh и confidential credentials остаются
            на сервере.
          </p>
        </div>
        <nav className="doc-tabs" aria-label="Раздел документации">
          <button
            aria-pressed={section === "setup"}
            onClick={() => setSection("setup")}
          >
            01 · Пошаговая настройка
          </button>
          <button
            aria-pressed={section === "flows"}
            onClick={() => setSection("flows")}
          >
            02 · Как работает SPA
          </button>
        </nav>
        {section === "setup" ? (
          <div className="doc-layout">
            <nav className="doc-steps" aria-label="Шаги настройки">
              {steps.map((title, index) => (
                <button
                  key={title}
                  aria-current={step === index ? "step" : undefined}
                  onClick={() => setStep(index)}
                >
                  <span>{String(index + 1).padStart(2, "0")}</span>
                  {title}
                </button>
              ))}
              <p>
                Параметры обрабатываются только в этой вкладке, не сохраняются и
                не отправляются на сервер. Секреты сюда вводить не нужно.
              </p>
            </nav>
            <article className="doc-panel">
              <p className="doc-kicker">
                ШАГ {step + 1} ИЗ {steps.length}
              </p>
              <h2>{steps[step]}</h2>
              {step === 0 && (
                <>
                  <p>
                    Браузер обращается по HTTPS к балансировщику, далее YARP/BFF
                    и приложения работают по HTTP. Каждая SPA использует свой
                    публичный origin и OIDC client.
                  </p>
                  <p>
                    Resource API и OpenID Provider — отдельные серверы. Пакет
                    проксирует только маршруты из конфигурации YARP.
                  </p>
                  <div className="doc-field-grid">
                    {fields.map(([key, label]) => (
                      <label key={key}>
                        {label}
                        <input
                          value={setup[key]}
                          autoComplete="off"
                          spellCheck={false}
                          onChange={(e) =>
                            setSetup({ ...setup, [key]: e.target.value })
                          }
                        />
                      </label>
                    ))}
                  </div>
                  {others.map((client, index) => (
                    <fieldset key={index}>
                      <legend>SPA {index + 2}</legend>
                      <div className="doc-field-grid">
                        {fields
                          .filter(
                            ([key]) =>
                              ![
                                "application",
                                "environment",
                                "namespace",
                              ].includes(key),
                          )
                          .map(([key, label]) => {
                            const clientKey = key as keyof ClientSetup;
                            return (
                              <label key={key}>
                                SPA {index + 2} · {label}
                                <input
                                  value={client[clientKey]}
                                  autoComplete="off"
                                  spellCheck={false}
                                  onChange={(e) =>
                                    setOthers(
                                      others.map((value, at) =>
                                        at === index
                                          ? { ...value, [key]: e.target.value }
                                          : value,
                                      ),
                                    )
                                  }
                                />
                              </label>
                            );
                          })}
                      </div>
                      <button
                        type="button"
                        onClick={() =>
                          setOthers(others.filter((_, at) => at !== index))
                        }
                      >
                        Удалить SPA {index + 2}
                      </button>
                    </fieldset>
                  ))}
                  <button
                    type="button"
                    onClick={() =>
                      setOthers([
                        ...others,
                        {
                          ...initialSetup,
                          name: `spa${others.length + 2}`,
                          origin: `https://spa${others.length + 2}.example.com`,
                          clientId: `spa${others.length + 2}-bff`,
                        },
                      ])
                    }
                  >
                    Добавить SPA
                  </button>
                  {!valid && (
                    <div role="alert" className="doc-note doc-error">
                      <strong>Проверьте параметры</strong>
                      <ul>
                        {errors.map((error) => (
                          <li key={error}>{error}</li>
                        ))}
                      </ul>
                    </div>
                  )}
                  <p className="doc-note">
                    Это проверка формата, а не проверка подключения. DNS, TLS,
                    доступность Redis, регистрация клиента и права на API
                    проверяются при запуске и ручном проходе сценария.
                  </p>
                </>
              )}
              {step === 1 && (
                <>
                  <p>
                    Зарегистрируйте <strong>confidential web client</strong>.
                    Разрешите Authorization Code, Refresh Token и PKCE S256. SPA
                    не регистрируется как public OAuth client и не получает
                    client secret.
                  </p>
                  <Code label="Redirect URI" value={callback} />
                  <Code
                    label="Post logout redirect URI"
                    value={logoutCallback}
                  />
                  {valid &&
                    others.map((client) => (
                      <section key={client.name}>
                        <h3>
                          {client.name} · {client.origin}
                        </h3>
                        <Code
                          label={`Redirect URI · ${client.name}`}
                          value={
                            new URL(
                              createRoutes(client.pathBase).loginCallback,
                              client.origin,
                            ).href
                          }
                        />
                        <Code
                          label={`Post logout redirect URI · ${client.name}`}
                          value={
                            new URL(
                              createRoutes(client.pathBase).logoutCallback,
                              client.origin,
                            ).href
                          }
                        />
                      </section>
                    ))}
                  <p>
                    Зарегистрируйте confidential client с Authorization Code +
                    PKCE, refresh grant и нужными API scopes. Провайдер должен
                    иметь browser login UI и продолжать исходный authorization
                    request после входа.
                  </p>
                  <p>
                    Authority и callback URI используют публичный HTTPS. Для
                    OpenIddict нужны permissions используемых endpoints, grants,
                    response type code и API scopes.
                  </p>
                  <a href="https://documentation.openiddict.com/guides/">
                    Настройка OpenIddict ↗
                  </a>
                </>
              )}
              {step === 2 && (
                <>
                  <p>
                    Нужен Redis 6.2+. Пакет создаёт подключение и хранит сессии
                    и общий key ring в Redis. На всех репликах должны совпадать
                    Redis, область ключей и EncryptionKey.
                  </p>
                  <Code
                    label="Настройки через secret provider / environment"
                    value={`ASPNETCORE_ENVIRONMENT=${setup.environment}\nConnectionStrings__Redis=<Redis connection>\nBff__EncryptionKey=<постоянный случайный секрет>\n${[setup, ...others].map((client) => `Bff__Clients__${client.name}__OpenIdConnect__ClientSecret=<OIDC client secret>`).join("\n")}`}
                  />
                  <p>
                    Сгенерируйте EncryptionKey один раз через
                    <code> openssl rand -base64 32</code> и сохраняйте между
                    redeploy. Секреты остаются в серверной конфигурации.
                  </p>
                  <a href="https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/blob/main/docs/SESSIONS.md">
                    Шифрование, смена ключа и ошибки Redis ↗
                  </a>
                </>
              )}
              {step === 3 && (
                <>
                  <p>
                    Подключите пакет и передайте корневую конфигурацию одним
                    вызовом. YARP, Redis, forwarded headers и все BFF clients
                    собираются внутри пакета.
                  </p>
                  {valid ? (
                    <Code
                      label="appsettings.json — без секретов"
                      value={createConfiguration(setup, others)}
                    />
                  ) : (
                    <p role="alert" className="doc-note doc-error">
                      Сначала исправьте параметры на шаге «Топология».
                    </p>
                  )}
                  <Code label="Program.cs" value={hostCode} />
                  <p>
                    Замените KnownProxies на IP непосредственного
                    балансировщика. Он сохраняет публичный Host и префикс
                    приложения, передаёт X-Forwarded-Proto=https и
                    перезаписывает forwarded headers. YARP и backend работают по
                    HTTP в доверенной внутренней сети.
                  </p>
                  <p>
                    В ReverseProxy — штатные маршруты и destinations. В
                    Bff:Clients:Routes — привязки по RouteId, AllowAnonymous для
                    SPA assets и необязательная CookiePolicy. Пакет сам
                    подключает middleware и MapReverseProxy.
                  </p>
                  <p>
                    Для размещения под путём согласуйте BFF PathBase, SPA base и
                    callback URI. Сохраните префикс на балансировщике.
                  </p>
                  <details>
                    <summary>Прикладная cookie для внешнего callback</summary>
                    <p>
                      CookiePolicy нужна только приложениям с техническими
                      state/correlation cookies. Start защищён сессией и X-CSRF;
                      точный callback разрешает внешнюю навигацию, а backend
                      проверяет state.
                    </p>
                    {valid && (
                      <Code
                        label="Cookie policy и routes — добавить к конфигурации"
                        value={createCookieConfiguration(setup)}
                      />
                    )}
                    <a href="https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/blob/main/docs/COOKIES.md">
                      Контракт прикладных cookies ↗
                    </a>
                  </details>
                  <p>
                    <a href="https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/blob/main/docs/BR-ADMIN-IDENTITY.md">
                      Готовый профиль BR.Admin-Identity ↗
                    </a>
                  </p>
                </>
              )}
              {step === 4 && (
                <>
                  <p>
                    Устанавливайте <code>X-CSRF: 1</code> для всех BFF data
                    endpoints и проксируемых API, включая GET. CORS-доступ к
                    session API с другого origin не предусмотрен.
                  </p>
                  <p>
                    Login вызывайте навигацией. Logout отправляйте native POST
                    form. ReturnUrl принимает локальный путь с query/fragment.
                    Внешний URL или повтор параметра отклоняется до
                    login/logout.
                  </p>
                  <Code
                    label="Login и чтение сессии"
                    value={createSpaCode(setup.pathBase)}
                  />
                  <p>
                    Sample использует официальный{" "}
                    <code>@tanstack/react-query</code>, а не OAuth SDK для
                    public SPA. <code>src/session.ts</code> обновляет данные при
                    focus/reconnect. Авторизованная видимая вкладка проверяет
                    сессию каждые 60 секунд.
                  </p>
                  <p>
                    Автоматические retries и фоновый polling отключены. Absolute
                    session lifetime ограничивает сервер.
                  </p>
                  <Code
                    label="Native POST logout с framework antiforgery token"
                    value={`const response = await fetch(${JSON.stringify(targetRoutes.antiforgery)}, {\n  credentials: "same-origin", headers: { "X-CSRF": "1" },\n});\nif (!response.ok) throw new Error(\`BFF returned \${response.status}\`);\nconst { requestToken, formFieldName } = await response.json();\nconst form = document.createElement("form");\nform.method = "POST";\nform.action = ${JSON.stringify(targetRoutes.logout)};\nconst field = document.createElement("input");\nfield.type = "hidden";\nfield.name = formFieldName;\nfield.value = requestToken;\nform.append(field);\ndocument.body.append(form);\nform.submit();`}
                  />
                  <p>
                    Перед logout отменяйте текущие query и блокируйте новые
                    UI-запросы. После 401 очищайте кэш пользователя и API. После
                    503 показывайте недоступность, без автоматического повтора
                    POST/refresh или перенаправления в бесконечный login.
                  </p>
                </>
              )}
              {step === 5 && (
                <>
                  <p>
                    Проверки ниже выполняет оператор на своём окружении. Эта
                    страница не подключается к вашим серверам и не подтверждает
                    работоспособность конфигурации.
                  </p>
                  <div className="doc-checklist">
                    {[
                      "Login с пустыми cookies проходит browser login провайдера и возвращает в SPA.",
                      "Session/API с cookie и X-CSRF работают; API проверяет audience, scopes и роли.",
                      "Без сессии — 401, при нарушении CSRF/авторизации — 403, при недоступности Redis — 503.",
                      "Native logout завершает локальную сессию; следующий запрос требует нового входа.",
                      "Балансировщик передаёт правильные scheme/host; у реплик общие Redis и EncryptionKey.",
                    ].map((item) => (
                      <label key={item}>
                        <input type="checkbox" />
                        {item}
                      </label>
                    ))}
                  </div>
                  <a href="https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/blob/main/docs/TESTING.md">
                    Автотесты и сборка пакета ↗
                  </a>
                </>
              )}
              <div className="doc-pagination">
                <button
                  className="secondary"
                  disabled={step === 0}
                  onClick={() => setStep(step - 1)}
                >
                  ← Назад
                </button>
                <span>
                  {step + 1} / {steps.length}
                </span>
                {step < steps.length - 1 ? (
                  <button onClick={() => setStep(step + 1)}>Далее →</button>
                ) : (
                  <button onClick={() => setSection("flows")}>
                    Как работает SPA →
                  </button>
                )}
              </div>
            </article>
          </div>
        ) : (
          <article className="doc-panel doc-explainer">
            <p className="doc-kicker">ЖИЗНЕННЫЙ ЦИКЛ СЕССИИ</p>
            <h2>От первого входа до logout</h2>
            <div className="doc-flow-tabs" aria-label="Сценарии SPA">
              {(Object.keys(flows) as (keyof typeof flows)[]).map((key) => (
                <button
                  key={key}
                  aria-pressed={flow === key}
                  onClick={() => {
                    setFlow(key);
                    setFlowStep(0);
                  }}
                >
                  {flows[key].title}
                </button>
              ))}
            </div>
            <h3>{current.title}</h3>
            <p>{current.summary}</p>
            <div className="doc-flow-layout">
              <nav className="doc-flow-steps" aria-label="Этапы сценария">
                {current.steps.map(([title], index) => (
                  <button
                    key={title}
                    aria-current={flowStep === index ? "step" : undefined}
                    onClick={() => setFlowStep(index)}
                  >
                    <span>{index + 1}</span>
                    {title}
                  </button>
                ))}
              </nav>
              <div className="doc-flow-detail" aria-live="polite">
                <p className="doc-kicker">
                  ЭТАП {flowStep + 1} / {current.steps.length}
                </p>
                <h3>{detail[0]}</h3>
                <p>{detail[1]}</p>
                <p className="doc-note">{detail[2]}</p>
                <div className="doc-pagination">
                  <button
                    className="secondary"
                    disabled={flowStep === 0}
                    onClick={() => setFlowStep(flowStep - 1)}
                  >
                    Предыдущий этап
                  </button>
                  <button
                    disabled={flowStep === current.steps.length - 1}
                    onClick={() => setFlowStep(flowStep + 1)}
                  >
                    Следующий этап
                  </button>
                </div>
              </div>
            </div>
            <p>
              <a href="https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp/blob/main/docs/SESSIONS.md">
                Подробнее о сессиях, конкурентном refresh и ошибках
                инфраструктуры ↗
              </a>
            </p>
          </article>
        )}
      </main>
      <footer className="doc-footer">
        <span>MonixOne.OidcProxy.Yarp · LGPL-3.0-only</span>
        <span>Статическая документация · секреты не запрашиваются</span>
      </footer>
    </div>
  );
}
