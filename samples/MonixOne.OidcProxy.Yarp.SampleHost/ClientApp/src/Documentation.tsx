import { useEffect, useState } from "react";
import {
  createConfiguration,
  createCookieConfiguration,
  createSpaCode,
  flows,
  hostCode,
  initialSetup,
  validateClients,
  validateSetup,
  type ClientSetup,
  type Setup,
} from "./documentationContent";
import {
  gatewayHostCode,
  inspectRoute,
  pipeline,
  presets,
  type RouteScenario,
} from "./documentationModel";
import { Architecture, Sequence } from "./DocumentationDiagram";
import { createRoutes } from "./routes";
import "./documentation.css";

const completeAppsettings = import.meta.env.VITE_DOCUMENTED_APPSETTINGS as string;

const sections = [
  ["setup", "Подключение", "Конфигурация и код"],
  ["settings", "Все настройки", "appsettings.json с описаниями"],
  ["flow", "Как работает", "Схемы и порядок middleware"],
  ["routes", "Маршруты", "BFF и native API"],
  ["sessions", "Сессии и cookies", "Redis, реплики, refresh"],
  ["checks", "Проверка и ошибки", "Локально и в CI"],
] as const;
type Section = (typeof sections)[number][0];
const selectedSection = (): Section =>
  sections.find(([id]) => `#${id}` === window.location.hash)?.[0] ?? "setup";
const fieldLabels: Record<keyof Setup, string> = {
  name: "Имя SPA",
  origin: "SPA / BFF origin",
  pathBase: "PathBase",
  authority: "OIDC Authority",
  clientId: "Client ID",
  api: "Backend URL",
  apiPath: "API path",
  application: "ApplicationName",
  environment: "Окружение",
  namespace: "KeyNamespace",
  redisKey: "Ключ подключения Redis",
  scopes: "Scopes",
};

function Code({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState<string | null>(null);
  const [error, setError] = useState(false);
  async function copy() {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(value);
      setError(false);
    } catch {
      setError(true);
    }
  }
  return (
    <section className="doc-code" aria-label={label}>
      <div className="code-heading">
        <strong>{label}</strong>
        <button type="button" onClick={() => void copy()}>
          {copied === value ? "Скопировано" : "Копировать"}
        </button>
      </div>
      <pre>
        <code>{value}</code>
      </pre>
      {error && (
        <p role="status">
          Буфер недоступен. Выделите и скопируйте код вручную.
        </p>
      )}
    </section>
  );
}

function RouteInspector() {
  const [scenario, setScenario] = useState<RouteScenario>({
    profile: true,
    route: "bff-api",
    session: true,
    csrf: true,
    foreign: false,
    bearer: true,
    scope: true,
    redis: true,
  });
  const result = inspectRoute(scenario);
  const controls: [keyof Omit<RouteScenario, "route">, string][] = [
    ["profile", "Профиль SPA подключён"],
    ["session", "Действующая BFF-сессия"],
    ["csrf", "X-CSRF: 1"],
    ["foreign", "Чужой Origin"],
    ["bearer", "Валидный native Bearer"],
    ["scope", "Нужный native scope"],
    ["redis", "Redis доступен"],
  ];
  return (
    <div className="route-inspector">
      <div>
        <label className="doc-field">
          Тип запроса
          <select
            value={scenario.route}
            onChange={(event) =>
              setScenario({
                ...scenario,
                route: event.target.value as RouteScenario["route"],
              })
            }
          >
            <option value="bff-api">Защищённый BFF API</option>
            <option value="native-api">Защищённый native API на host GW</option>
            <option value="spa">Anonymous SPA / assets</option>
            <option value="callback">Точный GET anonymous callback</option>
            <option value="auth">Защищённый /.auth/me</option>
          </select>
        </label>
        <div className="doc-checks">
          {controls.map(([key, label]) => (
            <label key={key}>
              <input
                type="checkbox"
                checked={scenario[key]}
                onChange={(event) =>
                  setScenario({
                    ...scenario,
                    [key]: event.target.checked,
                  })
                }
              />
              {label}
            </label>
          ))}
        </div>
      </div>
      <div className="route-result" role="status" aria-live="polite">
        <small>Решение GW</small>
        <strong>{result.result}</strong>
        <p>{result.detail}</p>
        <span>
          Модель документированного маршрута. Сеть, TLS и ответы ваших сервисов
          не проверяются.
        </span>
      </div>
    </div>
  );
}

export default function Documentation() {
  const [section, setSection] = useState<Section>(selectedSection);
  const [setup, setSetup] = useState<Setup>(initialSetup);
  const [others, setOthers] = useState<ClientSetup[]>([]);
  const [native, setNative] = useState(false);
  const [cookies, setCookies] = useState(false);
  const [code, setCode] = useState<"config" | "host" | "secrets" | "spa">(
    "config",
  );
  const [flow, setFlow] = useState<keyof typeof flows>("login");
  const [flowStep, setFlowStep] = useState(0);
  const [middleware, setMiddleware] = useState(0);
  useEffect(() => {
    const previousTitle = document.title;
    document.title = "MonixOne.OidcProxy.Yarp — справочник";
    const onHash = () => setSection(selectedSection());
    window.addEventListener("hashchange", onHash);
    return () => {
      window.removeEventListener("hashchange", onHash);
      document.title = previousTitle;
    };
  }, []);
  const errors = native
    ? validateSetup({
        ...initialSetup,
        name: setup.name,
        environment: setup.environment,
        origin: setup.origin,
        api: setup.api,
        apiPath: setup.apiPath,
        redisKey: setup.redisKey,
      })
    : validateClients(setup, others);
  const valid = errors.length === 0;
  const clients = native ? [] : [setup, ...others];
  const paths = createRoutes(setup.pathBase);
  const currentFlow = flows[flow];
  const detail = currentFlow.steps[flowStep]!;
  const secrets = [
    `ASPNETCORE_ENVIRONMENT=${setup.environment}`,
    `${setup.redisKey.replaceAll(":", "__")}=<Redis connection>`,
    ...(!native
      ? [
          "Bff__EncryptionKey=<постоянный случайный секрет>",
          ...clients.map(
            (client) =>
              `Bff__Clients__${client.name}__OpenIdConnect__ClientSecret=<секрет confidential client>`,
          ),
        ]
      : []),
  ].join("\n");
  const fields = (keys: (keyof Setup)[]) =>
    keys.map((key) => (
      <label className="doc-field" key={key}>
        {fieldLabels[key]}
        <input
          value={setup[key]}
          spellCheck={false}
          autoComplete="off"
          onChange={(event) =>
            setSetup({ ...setup, [key]: event.target.value })
          }
        />
      </label>
    ));

  return (
    <div className="docs">
      <header className="doc-header">
        <a
          className="doc-brand"
          href="#setup"
          onClick={() => setSection("setup")}
        >
          <span className="brand-symbol" aria-hidden="true">
            M
          </span>
          <span>
            MonixOne
            <span className="brand-package">OidcProxy.Yarp</span>
          </span>
        </a>
        <div className="doc-meta">
          <span className="version-label">
            v{import.meta.env.VITE_PACKAGE_VERSION ?? "8.x"}
          </span>
          <span>.NET 10</span>
          <a href="https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp">
            Исходный код ↗
          </a>
        </div>
      </header>
      <div className="doc-shell">
        <aside className="doc-sidebar">
          <p className="eyebrow">СПРАВОЧНИК</p>
          <nav aria-label="Разделы документации">
            {sections.map(([id, title, subtitle], index) => (
              <a
                href={`#${id}`}
                key={id}
                aria-current={section === id ? "page" : undefined}
                onClick={() => setSection(id)}
              >
                <span className="nav-number">0{index + 1}</span>
                <span>
                  <strong>{title}</strong>
                  <small>{subtitle}</small>
                </span>
              </a>
            ))}
          </nav>
          <p className="sidebar-note">
            Без сервисов и секретов.
            <br />
            Всё вычисляется в этой вкладке.
          </p>
        </aside>
        <main className="doc-main" id={section}>
          <div className="doc-section-heading">
            <p className="eyebrow">OIDC · YARP · REDIS</p>
            <h1>{sections.find(([id]) => id === section)?.[1]}</h1>
            <p>
              {section === "setup"
                ? "Минимальные изменения в GW. Конфигурация, callbacks и код — в одном месте."
                : section === "settings"
                  ? "Полный appsettings.json: назначение каждого параметра, границы конфигурации и серверные секреты."
                : section === "flow"
                  ? "Выберите компонент или этап. Схема показывает, где проходят запросы, cookies и токены."
                  : section === "routes"
                    ? "Одна регистрация YARP, разные границы доступа. Native API сохраняют свои JWT и политики."
                    : section === "sessions"
                      ? "Что хранится на сервере, как работают реплики и что происходит при отказе."
                      : "Самодостаточные тесты и проверка своего окружения без ложного подтверждения готовности."}
            </p>
          </div>

          {section === "setup" && (
            <>
              <div className="doc-toolbar">
                <div className="segmented" aria-label="Шаблоны">
                  {Object.entries(presets).map(([key, preset]) => (
                    <button
                      type="button"
                      key={key}
                      onClick={() => {
                        setSetup({
                          ...preset.setup,
                        });
                        setOthers([]);
                        setNative(false);
                        setCookies(key !== "generic");
                      }}
                    >
                      {preset.title}
                    </button>
                  ))}
                </div>
                <label className="doc-toggle">
                  <input
                    type="checkbox"
                    checked={native}
                    onChange={(event) => setNative(event.target.checked)}
                  />
                  Без SPA / native GW
                </label>
              </div>
              <div className="setup-layout">
                <section
                  className="setup-fields"
                  aria-label="Параметры конфигурации"
                >
                  <h2>1. Публичные адреса</h2>
                  <div className="doc-field-grid">
                    {fields(
                      native
                        ? ["origin", "api", "apiPath"]
                        : [
                            "origin",
                            "pathBase",
                            "authority",
                            "clientId",
                            "api",
                            "apiPath",
                          ],
                    )}
                  </div>
                  <details>
                    <summary>Имена, окружение и Redis</summary>
                    <div className="doc-field-grid">
                      {fields(
                        native
                          ? ["name", "environment", "redisKey"]
                          : [
                              "name",
                              "application",
                              "environment",
                              "namespace",
                              "redisKey",
                              "scopes",
                            ],
                      )}
                    </div>
                  </details>
                  {!native && (
                    <>
                      <label className="doc-toggle">
                        <input
                          type="checkbox"
                          checked={cookies}
                          onChange={(event) => setCookies(event.target.checked)}
                        />
                        ForwardCookies — доверять cookies backend этих SPA
                      </label>
                      {others.map((client, index) => (
                        <details key={index} open>
                          <summary>
                            SPA {index + 2} · {client.name}
                          </summary>
                          <div className="doc-field-grid">
                            {(
                              [
                                "name",
                                "origin",
                                "pathBase",
                                "authority",
                                "clientId",
                                "api",
                                "apiPath",
                                "scopes",
                              ] as const
                            ).map((key) => (
                              <label className="doc-field" key={key}>
                                SPA {index + 2} · {fieldLabels[key]}
                                <input
                                  value={client[key]}
                                  spellCheck={false}
                                  autoComplete="off"
                                  onChange={(event) =>
                                    setOthers(
                                      others.map((value, at) =>
                                        at === index
                                          ? {
                                              ...value,
                                              [key]: event.target.value,
                                            }
                                          : value,
                                      ),
                                    )
                                  }
                                />
                              </label>
                            ))}
                          </div>
                          <button
                            type="button"
                            onClick={() =>
                              setOthers(others.filter((_, at) => at !== index))
                            }
                          >
                            Удалить SPA {index + 2}
                          </button>
                        </details>
                      ))}
                      <button
                        className="subtle-button"
                        type="button"
                        onClick={() => {
                          let number = 2;
                          while (
                            [setup, ...others].some(
                              (client) => client.name === `spa${number}`,
                            )
                          )
                            number++;
                          setOthers([
                            ...others,
                            {
                              ...initialSetup,
                              name: `spa${number}`,
                              origin: `https://spa${number}.example.com`,
                              clientId: `spa${number}-bff`,
                            },
                          ]);
                        }}
                      >
                        + Добавить SPA
                      </button>
                    </>
                  )}
                  {!valid && (
                    <div role="alert" className="doc-notice error">
                      <strong>Исправьте параметры</strong>
                      <ul>
                        {errors.map((error) => (
                          <li key={error}>{error}</li>
                        ))}
                      </ul>
                    </div>
                  )}
                  <p className="doc-hint">
                    Сюда вводятся только публичные параметры. Пароли, токены и
                    ключи не запрашиваются, не сохраняются и не отправляются.
                  </p>
                </section>
                <section
                  className="setup-output"
                  aria-label="Сгенерированная конфигурация"
                >
                  <h2>2. Подключение</h2>
                  <div
                    className="segmented code-tabs"
                    aria-label="Результат генерации"
                  >
                    {(
                      [
                        ["config", "appsettings"],
                        ["host", "GW / Program.cs"],
                        ["secrets", "Секреты"],
                        ["spa", "SPA-клиент"],
                      ] as const
                    ).map(([key, label]) => (
                      <button
                        type="button"
                        key={key}
                        aria-pressed={code === key}
                        onClick={() => setCode(key)}
                      >
                        {label}
                      </button>
                    ))}
                  </div>
                  {!valid ? (
                    <p className="doc-notice">
                      Конфигурация появится после исправления параметров.
                    </p>
                  ) : code === "config" ? (
                    <>
                      <Code
                        label="appsettings.json — без секретов"
                        value={createConfiguration(
                          setup,
                          native ? [] : others,
                          {
                            native,
                            forwardCookies: cookies,
                          },
                        )}
                      />
                      <p className="doc-hint">
                        Это фрагмент для слияния. Сохраните существующие routes,
                        clusters, CORS origins, JWT/scope и rate-limit настройки
                        GW. Замените 10.0.0.10 реальным IP ближайшего proxy.
                      </p>
                    </>
                  ) : code === "host" ? (
                    <>
                      <Code
                        label="Подключение в существующем GW"
                        value={gatewayHostCode}
                      />
                      <details>
                        <summary>
                          Полный Program.cs для отдельного sample host
                        </summary>
                        <Code label="Program.cs standalone" value={hostCode} />
                      </details>
                      <p className="doc-hint">
                        Native auth, rate limiting и observability
                        регистрируются расширениями вашего GW. AddReverseProxy,
                        MapReverseProxy и middleware пакета повторять не нужно.
                      </p>
                    </>
                  ) : code === "secrets" ? (
                    <>
                      <Code
                        label="Environment / secret provider"
                        value={secrets}
                      />
                      <p className="doc-hint">
                        EncryptionKey создаётся один раз:{" "}
                        <code>openssl rand -base64 32</code>. На репликах должны
                        совпадать Redis, namespace и ключ. Настройки BFF
                        фиксируются при старте.
                      </p>
                    </>
                  ) : (
                    <>
                      <Code
                        label="Login и чтение сессии"
                        value={createSpaCode(setup.pathBase)}
                      />
                      <p className="doc-hint">
                        Login — навигация. Logout — antiforgery и native POST
                        form. Готовая реализация: <code>src/bff.ts</code> в
                        sample.
                      </p>
                    </>
                  )}
                </section>
              </div>
              {!native && valid && (
                <section className="doc-block">
                  <div className="block-heading">
                    <h2>3. Регистрация в Identity</h2>
                    <span>Confidential · Code + PKCE S256</span>
                  </div>
                  <p>
                    Разрешите
                    authorization/token/refresh/revocation/end-session, response
                    type <code>code</code> и нужные scopes. Каждый client_id
                    регистрируется отдельно; API проверяет audience и права.
                  </p>
                  <div className="callback-grid">
                    {clients.map((client) => (
                      <div key={client.name}>
                        <strong>{client.name}</strong>
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
                      </div>
                    ))}
                  </div>
                  <details>
                    <summary>BR.Admin-Identity: Local и Dev</summary>
                    <p>
                      У клиента <code>br-admin-identity-bff</code> должен быть
                      tenant mapping Identity. В админке React email/OTP создаёт
                      cookie browser session Identity; затем вход проходит через
                      GW Code/PKCE. Razor UI в Identity не нужен. Mobile OTP
                      grant этот этап не заменяет.
                    </p>
                    <p>
                      Для Local: GW <code>https://localhost:8443/admin/</code>,
                      Identity <code>https://localhost:8444/</code>, SPA +
                      Calendar API <code>http://127.0.0.1:8000/</code>.
                      Запускайте <code>npm run start:calendar</code>: сервер
                      только статики возвращает 404 на Calendar API.
                    </p>
                    <p>
                      В BR.Admin-Identity общий <code>.env.local</code>:
                      VITE_IDENTITY_ORIGIN, APP_URL, IDENTITY_ISSUER,
                      IDENTITY_JWKS_URL и IDENTITY_AUDIENCE. Identity
                      Cors:Origins должен разрешать HTTPS origin GW. Callback
                      localhost регистрируется только в Local.
                    </p>
                    <p>
                      Публичный Dev:{" "}
                      <code>
                        https://admin.id-dev.intra-beetroute.ru/admin/
                      </code>
                      . Готовые appsettings, ingress и tenant fragment остаются
                      в <code>docs/examples/br-admin-identity/</code>. Для SPA
                      assets нужен отдельный anonymous catch-all route, для
                      user-management — свой cluster Identity.
                    </p>
                  </details>
                </section>
              )}
              {native && (
                <div className="doc-notice">
                  <strong>SPA не подключена</strong>
                  <p>
                    В конфигурации нет Bff:Clients. Пакет сохраняет native YARP,
                    JWT policies и балансировку. Оставшийся ClientSecret без
                    PublicOrigin BFF не включает. Redis здесь нужен только
                    компонентам GW, которые сами его используют.
                  </p>
                </div>
              )}
            </>
          )}

          {section === "settings" && (
            <>
              <section className="doc-block">
                <h2>Полный appsettings.json</h2>
                <p>
                  Пример для нового внедрения BR.Admin-Identity. Каждый параметр
                  снабжён комментарием: назначение, единицы и ограничения.
                  ASP.NET Core поддерживает комментарии в appsettings.json.
                  Замените адреса, scopes и доверенные proxy для своего окружения.
                </p>
                <Code label="appsettings.json — все настройки с описаниями" value={completeAppsettings} />
              </section>
              <section className="doc-block">
                <h2>Секреты и окружение</h2>
                <p>
                  Пустые EncryptionKey, ClientSecret и строка Redis заполняются
                  через secret provider или environment с разделителем __.
                  EnvironmentName берётся из IHostEnvironment. Конфигурация BFF
                  фиксируется при запуске; изменения сроков, клиентов и секретов
                  применяются согласованным перезапуском реплик.
                </p>
                <Code label="Имена серверных переменных" value={`ASPNETCORE_ENVIRONMENT\nConnectionStrings__Redis\nBff__EncryptionKey\nBff__Clients__adminidentity__OpenIdConnect__ClientSecret`} />
              </section>
              <section className="doc-block">
                <h2>Стандартные настройки host</h2>
                <p>
                  Срок ключей Data Protection задаётся штатным SetDefaultKeyLifetime.
                  ProtectKeysWithCertificate и UnprotectKeysWithAnyCertificate
                  управляют сертификатами защиты key ring; EncryptionKey тогда не нужен.
                  Эти API подключает host: несуществующих JSON-параметров пакета для них нет.
                  Настройки exporters OpenTelemetry, health checks, rate limiting,
                  native JWT policies и дополнительного middleware также принадлежат host.
                </p>
                <p>
                  ReverseProxy использует полный штатный контракт YARP. В примере
                  показаны маршруты и destinations; дополнительные transport,
                  load balancing и health-check параметры остаются настройками YARP.
                  OpenIdConnect использует стандартные OpenIdConnectOptions;
                  фиксированные требования BFF перечислены в комментариях примера.
                  Новое хранение сессий не содержит читателя или мигратора формата 8.x.
                </p>
              </section>
            </>
          )}

          {section === "flow" && (
            <>
              <div className="block-heading">
                <h2>Компоненты</h2>
                <label className="doc-toggle">
                  <input
                    type="checkbox"
                    checked={native}
                    onChange={(event) => setNative(event.target.checked)}
                  />
                  Без SPA
                </label>
              </div>
              <Architecture native={native} />
              <section className="doc-block">
                <div className="block-heading">
                  <h2>Жизненный цикл</h2>
                  <span>Нажмите на этап</span>
                </div>
                {native && (
                  <p className="doc-hint">
                    Сценарии ниже показывают режим с подключённой SPA. В native
                    режиме пакет не выполняет эти OIDC-обмены и не создаёт
                    BFF-сессии.
                  </p>
                )}
                <div className="segmented flow-tabs" aria-label="Сценарии">
                  {(Object.keys(flows) as (keyof typeof flows)[]).map((key) => (
                    <button
                      key={key}
                      type="button"
                      aria-pressed={flow === key}
                      onClick={() => {
                        setFlow(key);
                        setFlowStep(0);
                      }}
                    >
                      {key === "connections"
                        ? "Calendar / cookies"
                        : flows[key].title}
                    </button>
                  ))}
                </div>
                <p>{currentFlow.summary}</p>
                <Sequence flow={flow} step={flowStep} title={detail[0]} />
                <div className="flow-layout">
                  <nav className="flow-steps" aria-label="Этапы сценария">
                    {currentFlow.steps.map(([title], index) => (
                      <button
                        type="button"
                        key={title}
                        aria-current={flowStep === index ? "step" : undefined}
                        onClick={() => setFlowStep(index)}
                      >
                        <span>{index + 1}</span>
                        {title}
                      </button>
                    ))}
                  </nav>
                  <div className="flow-detail" aria-live="polite">
                    <small>
                      Этап {flowStep + 1} / {currentFlow.steps.length}
                    </small>
                    <h3>{detail[0]}</h3>
                    <p>{detail[1]}</p>
                    <div className="doc-notice">{detail[2]}</div>
                    <div className="doc-pagination">
                      <button
                        type="button"
                        disabled={flowStep === 0}
                        onClick={() => setFlowStep(flowStep - 1)}
                      >
                        ← Назад
                      </button>
                      <button
                        type="button"
                        disabled={flowStep === currentFlow.steps.length - 1}
                        onClick={() => setFlowStep(flowStep + 1)}
                      >
                        Следующий этап →
                      </button>
                    </div>
                  </div>
                </div>
              </section>
              <section className="doc-block">
                <h2>Порядок middleware</h2>
                <div className="pipeline" aria-label="Порядок middleware">
                  {pipeline.map(([title], index) => (
                    <button
                      type="button"
                      key={title}
                      aria-pressed={middleware === index}
                      onClick={() => setMiddleware(index)}
                    >
                      <span>{index + 1}</span>
                      {title}
                    </button>
                  ))}
                </div>
                <div className="diagram-detail" aria-live="polite">
                  <strong>{pipeline[middleware]![0]}</strong>
                  <p>{pipeline[middleware]![1]}</p>
                </div>
              </section>
              <section className="doc-block">
                <h2>GW за балансировщиком: IP и HTTPS</h2>
                <p>
                  Пакет один раз настраивает и вызывает стандартный ASP.NET Core
                  middleware для X-Forwarded-For, X-Forwarded-Proto и
                  X-Forwarded-Host. RemoteIpAddress, схема и host восстанавливаются
                  до routing и callback GW с observability, CORS и rate limiting.
                  Повторные Configure/PostConfigure для ForwardedHeadersOptions
                  и UseForwardedHeaders в GW не нужны.
                </p>
                <Code
                  label="appsettings — закрытый GW за ingress"
                  value={`{
  "ForwardedHeaders": {
    "TrustAllProxies": true,
    "AllowedHosts": ["gw.example.com", "spa.example.com"]
  }
}`}
                />
                <p>
                  Это фрагмент конфигурации выбранного закрытого окружения.
                  TrustAllProxies не проверяет имя окружения: его нужно включить
                  явно. Опция очищает KnownProxies и KnownIPNetworks, сохраняя
                  ForwardLimit=1. Списки адресов и подсетей Swarm не нужны;
                  публикация через ingress, реплики и start-first сохраняются.
                  При false или отсутствии опции действует строгий режим.
                </p>
                <h3>Какой IP получает приложение</h3>
                <p>
                  Middleware берёт одно последнее значение X-Forwarded-For.
                  Например, для 203.0.113.66, 198.51.100.10 результатом будет
                  RemoteIpAddress=198.51.100.10. Это адрес, подтверждённый edge,
                  а не IP контейнера ingress. Rate limiter и прикладной код должны
                  читать HttpContext.Connection.RemoteIpAddress, не разбирать
                  первое значение заголовка. YARP передаёт нормализованный адрес
                  backend-сервису.
                </p>
                <p>
                  Шлюз должен перезаписывать недоверенные forwarded-заголовки
                  либо добавлять наблюдаемый адрес последним в X-Forwarded-For,
                  задавая публичные Proto и Host. В Nginx $proxy_add_x_forwarded_for
                  добавляет $remote_addr последним; вариант X-Forwarded-For
                  $remote_addr заменяет входящий список. Если перед Nginx стоит
                  ещё один балансировщик, доверие к исходному IP настраивается
                  на этом edge: пакет не определяет адрес за ним автоматически.
                </p>
                <div className="doc-notice">
                  Прямой недоверенный доступ к GW должен быть исключён. Иначе
                  отправитель сможет подменить forwarded IP, схему и host в
                  пределах AllowedHosts. Опция снимает только проверку IP proxy;
                  AllowedHosts, BFF origin checks, CORS, CSRF и аутентификация
                  остаются включёнными.
                </div>
                <p>
                  Identity — отдельный сервер авторизации со своим HTTP pipeline.
                  Его обработка forwarded headers не дублирует GW; подключать
                  BFF-пакет только ради неё не нужно.
                </p>
                <p className="doc-hint">
                  Подробности:{" "}
                  <a href="https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0">
                    ASP.NET Core proxy configuration
                  </a>
                  {" · "}
                  <a href="https://nginx.org/en/docs/http/ngx_http_proxy_module.html#var_proxy_add_x_forwarded_for">
                    Nginx forwarded-for
                  </a>
                </p>
              </section>
            </>
          )}

          {section === "routes" && (
            <>
              <h2>Проверить решение GW</h2>
              <p>
                Переключайте условия: модель показывает, какой запрос будет
                отклонён, а какой дойдёт до сервиса.
              </p>
              <RouteInspector />
              <section className="doc-block">
                <h2>Маршруты без дублирования</h2>
                <div className="table-scroll">
                  <table>
                    <thead>
                      <tr>
                        <th>Маршрут</th>
                        <th>Граница</th>
                        <th>Конфигурация</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr>
                        <td>SPA / assets</td>
                        <td>Anonymous, без серверного Bearer</td>
                        <td>
                          Точный host, catch-all последним, AuthorizationPolicy:
                          anonymous
                        </td>
                      </tr>
                      <tr>
                        <td>API приложения</td>
                        <td>Cookie + X-CSRF + server session</td>
                        <td>Точный host; клиент выбирается автоматически</td>
                      </tr>
                      <tr>
                        <td>Native API</td>
                        <td>Исходный JWT / scope policy</td>
                        <td>Существующий route на host gateway</td>
                      </tr>
                      <tr>
                        <td>OAuth callback приложения</td>
                        <td>Без BFF-сессии; backend state/cookie</td>
                        <td>Точные host + path + method, anonymous</td>
                      </tr>
                      <tr>
                        <td>{paths.user}</td>
                        <td>Claims текущей BFF-сессии</td>
                        <td>Регистрирует пакет; X-CSRF: 1</td>
                      </tr>
                    </tbody>
                  </table>
                </div>
                <p>
                  Смешанные Hosts и wildcard не получают BFF-токен. RouteId не
                  нужно повторять в Bff:Clients:Routes. Policies и балансировка
                  обычного YARP сохраняются.
                </p>
                <details>
                  <summary>Что важно для /admin и нескольких SPA</summary>
                  <p>
                    PublicOrigin не содержит пути. Для /admin задайте PathBase:
                    /admin, base SPA: /admin/ и callbacks с этим префиксом.
                    Балансировщик сохраняет префикс, пакет снимает его до
                    routing. Match.Path описывает путь после снятия PathBase.
                  </p>
                  <p>
                    Каждая SPA использует отдельный HTTPS host/port и свой
                    confidential client. Cookie names, серверные сессии и OIDC
                    handlers изолированы. Два клиента с одним effective origin
                    не допускаются.
                  </p>
                  <p>
                    Пустое поле Routes лучше полностью не задавать: явные
                    bindings имеют приоритет и сохраняют строгую выборочную
                    привязку. Корневую конфигурацию передавайте в AddOidcProxy,
                    а не секцию Bff.
                  </p>
                </details>
              </section>
            </>
          )}

          {section === "sessions" && (
            <>
              <div className="facts">
                <div>
                  <small>Idle по умолчанию</small>
                  <strong>20 минут</strong>
                  <span>От принятого защищённого запроса</span>
                </div>
                <div>
                  <small>Absolute по умолчанию</small>
                  <strong>8 часов</strong>
                  <span>Не продлевается refresh</span>
                </div>
                <div>
                  <small>Refresh budget</small>
                  <strong>30 секунд</strong>
                  <span>Один CAS-владелец на сессию</span>
                </div>
              </div>
              <details>
                <summary>Настроить сроки сессии</summary>
                <p>
                  У клиента задаются SessionIdleTimeout и SessionAbsoluteTimeout
                  в формате TimeSpan, например 00:20:00 и 08:00:00. Оба срока
                  положительные; absolute не меньше idle и не больше 365 дней.
                  RememberedSessionTimeout задаёт фиксированный срок явно выбранного
                  rememberMe=true (по умолчанию 30 дней), без короткого idle.
                  RefreshTimeout, SessionStoreTimeout и TokenRevocationTimeout
                  задают бюджеты refresh, записи/очистки сессии и revocation.
                  HTTP timeout IdP настраивается стандартным
                  OpenIdConnect:BackchannelTimeout.
                </p>
                <p>
                  Для явного «Запомнить» передайте rememberMe=true в /.auth/login
                  вместе с локальным returnUrl. Выбор защищён стандартным OIDC
                  state. Постоянная cookie и серверная сессия имеют общий
                  фиксированный срок RememberedSessionTimeout: по умолчанию
                  30 дней без короткого idle-лимита. Активность и refresh не
                  продлевают срок; logout, отзыв доступа и истечение refresh token
                  могут завершить вход раньше. Без выбора остаётся сессионная cookie.
                </p>
              </details>
              <details open>
                <summary>Что хранится и чем шифруется</summary>
                <p>
                  Сессию с ticket, claims и токенами защищает ASP.NET Core
                  Data Protection. Framework управляет ротацией ключей данных;
                  общий зашифрованный key ring хранится в Redis.
                </p>
                <p>
                  Cookie со случайной ссылкой, OIDC state и antiforgery защищает
                  тот же ASP.NET Core Data Protection. По умолчанию EncryptionKey
                  защищает descriptors через jose-jwt PBES2. Host может выбрать
                  стандартный ProtectKeysWithCertificate; пароль тогда не нужен. SessionKey
                  связывает payload с приложением, окружением, клиентом и
                  session ID.
                </p>
                <Code
                  label="Область Redis"
                  value={`{application}:{environment}:{namespace}:{client}:sessions:{client-slot}:{id}\n{application}:{environment}:{namespace}:{client}:session-index:{client-slot}:...\n{application}:{environment}:{namespace}:data-protection`}
                />
                <p>
                  Session TTL ограничен idle и absolute. У framework key ring
                  нет session TTL. Потеря key ring делает cookies/state
                  нечитаемыми. Нужен Redis 6.2+. Настройте TLS/ACL, persistence
                  и eviction Redis.
                </p>
              </details>
              <details>
                <summary>Реплики, refresh и logout</summary>
                <p>
                  На репликах совпадают Redis, ApplicationName, EnvironmentName,
                  KeyNamespace и EncryptionKey. EnvironmentName берётся у host;
                  сегменты Redis scope приводятся к lowercase. Разным окружениям
                  нужны отдельные scope и секреты.
                </p>
                <p>
                  Refresh начинается в окне min(30 секунд, 10% срока access
                  token). В реплике запросы ждут общую задачу; между репликами
                  владение определяется CAS. Отмена одного ожидающего не
                  отменяет остальные запросы. Неизвестный исход обмена не
                  повторяется, просроченный pending marker завершает сессию.
                </p>
                <p>
                  Logout выполняет GETDEL. Поздний refresh не восстанавливает
                  запись. Antiforgery/logout не ждут refresh. Revocation
                  выполняется best effort; уже отправленные API-запросы logout
                  не отменяет.
                </p>
                <p>
                  Статика, anonymous routes и antiforgery не продлевают idle.
                  Sample проверяет /.auth/me?slide=false в видимой вкладке:
                  запрос не продлевает idle и не запускает refresh. Прежний
                  /.auth/me без параметра сохраняет продление сессии.
                </p>
              </details>
              <details>
                <summary>Уведомления logout от Identity Provider</summary>
                <p>
                  Зарегистрируйте у поддерживающего OIDC Back-Channel Logout
                  провайдера URL PublicOrigin + PathBase + /.auth/backchannel-logout.
                  Endpoint принимает подписанный logout_token без браузерной
                  cookie и CSRF header. JWT проверяет стандартный валидатор Microsoft.
                </p>
                <p>
                  Сессии выбираются по issuer, клиенту и sid/sub. Индексы Redis
                  создаются атомарно с сессией; удаления и replay marker также
                  атомарны. Повторное уведомление не закрывает более поздний вход.
                  Незавершённое удаление возвращает 503 и допускает повтор IdP.
                  Для атомарных scripts ключи одного BFF-клиента используют общий
                  hash slot Redis Cluster; распределение по shard выполняется по клиентам.
                  Лимиты задаются в Bff:Clients:&lt;name&gt;:BackchannelLogout:
                  MaximumRequestBodyBytes, MaximumTokenLength, MaximumTokenAge,
                  ClockSkew, ProcessingTimeout, ReplayRetention, BatchSize и MaximumBatches.
                </p>
              </details>
              <details>
                <summary>Ротация ключей и восстановление Redis</summary>
                <p>
                  Ключи данных ротирует framework с сохранением старых ключей
                  для чтения. Для смены сертификата защиты key ring используйте
                  стандартные ProtectKeysWithCertificate и UnprotectKeysWithAnyCertificate.
                  Старые сертификаты нужны, пока ими защищены записи key ring.
                  Смена парольного EncryptionKey сама по себе не является ротацией.
                  Формат предназначен для нового внедрения; миграторов нет.
                </p>
                <p>
                  Асинхронный failover Redis или старый backup может вернуть удалённые
                  сессии и credentials. При потере состояния закройте прежнюю область
                  сессий и отзовите соответствующие grants у IdP. Шифрование и CAS
                  не обеспечивают защиту от отката сохранённых данных.
                </p>
                <p>
                  При потере подтверждения записи может остаться недоступная
                  сессия до TTL. Redis и IdP не образуют общей транзакции:
                  потерянный ответ IdP может оставить неизвестный BFF токен.
                  Результат revocation не гарантируется.
                </p>
              </details>
              <section className="doc-block">
                <h2>Cookies Calendar и других приложений</h2>
                <p>
                  Для доверенного backend достаточно{" "}
                  <code>ForwardCookies: true</code>. Имена cookies и
                  provider-specific paths в BFF перечислять не нужно.
                </p>
                {valid && (
                  <Code
                    label="Фрагмент cookie bridge и точного callback"
                    value={createCookieConfiguration(setup)}
                  />
                )}
                <details>
                  <summary>Гарантии и ограничения bridge</summary>
                  <ul>
                    <li>
                      Namespace разделяет BFF client и YARP cluster; backend
                      получает только исходные имена своего cluster.
                    </li>
                    <li>
                      BFF/OIDC cookies и произвольные browser
                      Cookie/Authorization не передаются.
                    </li>
                    <li>
                      Browser cookie имеет префикс __Host-Http-, Path=/ и не
                      содержит Domain. Secure и HttpOnly обязательны. Явный
                      SameSite сохраняется, отсутствующий становится Lax.
                    </li>
                    <li>
                      Backend Path вместе с PathBase проверяется сервером с
                      учётом регистра и границ сегментов. Scope кодируется в
                      browser имени; backend получает исходное имя и
                      непрозрачное значение. Max-Age/Expires не продлеваются.
                      Произвольные YARP rewrites автоматически не инвертируются.
                    </li>
                    <li>
                      Чужой origin не получает cookies на wildcard/public
                      routes. Исключение — точный anonymous callback с
                      ограниченными methods.
                    </li>
                    <li>
                      Backend проверяет state/nonce, TTL и однократность.
                      Повреждённый Set-Cookie — 502; дубли browser cookies —
                      400. Ответ с cookies получает no-store.
                    </li>
                  </ul>
                </details>
                <details>
                  <summary>Cookie tossing и обновление пакета</summary>
                  <p>
                    __Host- требует Secure, Path=/ и отсутствие Domain. Поэтому
                    совместимый браузер отвергает cookie, которую соседний
                    поддомен пытается установить для родительского домена.
                    __Host-Http- дополнительно требует установку через HTTP с
                    HttpOnly в поддерживающих этот префикс браузерах. Защита не
                    требует новых настроек GW, ключей или provider-specific
                    policies.
                  </p>
                  <p>
                    CookiePolicies также используют защищённый префикс и browser
                    Path=/; их Path ограничивает forwarding на сервере. Это не
                    расширяет доступ backend к cookies других клиентов, cluster
                    или путей.
                  </p>
                  <p>
                    Обновите все реплики: старый bridge не должен оставаться в
                    трафике. Прежние __Secure- cookies не принимаются;
                    незавершённое внешнее OAuth-подключение нужно начать заново.
                    Формат BFF-сессий и OAuth tokens не меняется. Старые cookies
                    можно дождаться по их TTL.
                  </p>
                  <p>
                    Cookies не изолируются по портам одного hostname. Для
                    недоверенных приложений используйте разные hostname. Bridge
                    не заменяет backend-проверку state/nonce, срока и
                    однократности и не устраняет XSS в самом origin приложения.
                  </p>
                </details>
                <details>
                  <summary>Узкие CookiePolicies: совместимость с 8.0</summary>
                  <p>
                    Для выборочного allowlist остаются явные Bff:Clients:Routes
                    и CookiePolicies. Политика route имеет приоритет над
                    ForwardCookies. Задаются ClusterId, Names (точные имена или
                    prefix*), серверный Path, SameSite и MaxAge — верхняя
                    граница persistent expiry; session cookie остаётся session
                    cookie.
                  </p>
                  <p>
                    Неизвестная политика или неправильный ClusterId отклоняются
                    при старте. Cross-site POST требует точного POST callback и
                    явного SameSite=None и в policy, и в backend cookie.
                    Отсутствующий SameSite не ослабляется до None.
                  </p>
                  <p>
                    При параллельных транзакциях backend создаёт уникальные
                    correlation names. Logout BFF не отменяет прикладные
                    OAuth-транзакции.
                  </p>
                </details>
              </section>
            </>
          )}

          {section === "checks" && (
            <>
              <div className="doc-notice">
                <strong>CI не требует запущенной связки сервисов</strong>
                <p>
                  GW использует TestServer, тестовые upstream и собственные
                  JWT-ключи/metadata. Identity запускается внутри
                  WebApplicationFactory. Redis/PostgreSQL создаются через
                  Testcontainers. Нужен рабочий Docker; production credentials
                  не нужны.
                </p>
              </div>
              <div className="check-columns">
                <div>
                  <h2>Автотесты</h2>
                  <Code
                    label="Пакет .NET"
                    value={`dotnet restore MonixOne.OidcProxy.Yarp.sln\ndotnet build tests/MonixOne.OidcProxy.Yarp.Tests -c Release -p:GeneratePackageOnBuild=false\npwsh tests/MonixOne.OidcProxy.Yarp.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps --only-shell chromium\ndotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release --no-build\nmake tools-restore\nmake format-check`}
                  />
                  <details>
                    <summary>Самодостаточные браузерные тесты</summary>
                    <p>
                      Microsoft.Playwright работает в том же xUnit-проекте.
                      WebApplicationFactory запускает пакет на HTTPS Kestrel;
                      Redis/PostgreSQL поднимаются Testcontainers, Identity и
                      provider/backend заменяются локальными HTTP fixtures.
                      Chromium использует отдельные browser contexts и тестовые
                      origins, привязанные к loopback; доверие ограничено
                      сертификатом fixture. Рабочие GW/Identity, Google и
                      production credentials не нужны.
                    </p>
                    <p>
                      Проверяются настоящий Code/PKCE-вход, попытки cookie
                      tossing через Set-Cookie и JavaScript соседнего
                      HTTPS-поддомена, нормальный cross-site Calendar callback,
                      однократность и удаление state cookie, directory API без
                      callback cookie, отказ от старого формата и серверные
                      границы Path.
                    </p>
                    <Code
                      label="Только браузерные сценарии"
                      value={`dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release --no-build -- --filter-class MonixOne.OidcProxy.Yarp.Tests.BrowserCookieBridgeTests`}
                    />
                    <p>
                      Браузер устанавливается один раз после build; CI делает
                      это явно перед тестами. Тесты не пропускаются при его
                      отсутствии. Если PowerShell недоступен, при установленном
                      Node можно вызвать тот же CLI:{" "}
                      <code>
                        node
                        tests/MonixOne.OidcProxy.Yarp.Tests/bin/Release/net10.0/.playwright/package/cli.js
                        install --only-shell chromium
                      </code>
                      . На Linux системные библиотеки браузера должны быть
                      установлены.
                    </p>
                  </details>
                  <Code
                    label="Sample и документация"
                    value={`cd samples/MonixOne.OidcProxy.Yarp.SampleHost/ClientApp\ncorepack pnpm install --frozen-lockfile\ncorepack pnpm test\ncorepack pnpm build\ncorepack pnpm docs:build`}
                  />
                  <p className="doc-hint">
                    SDK закреплён в global.json; Node/pnpm — в package.json.
                    Единственный C# formatter — CSharpier из tool manifest. Не
                    накладывайте dotnet format whitespace поверх него.
                  </p>
                  <details>
                    <summary>Сборка NuGet и sample</summary>
                    <Code
                      label="Сборка и упаковка"
                      value={`dotnet build src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release\ndotnet pack src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release --no-build -o artifacts/nuget\ndotnet publish samples/MonixOne.OidcProxy.Yarp.SampleHost -c Release`}
                    />
                    <p>
                      Перед CI потребителя пакет должен быть доступен в
                      NuGet-feed или как CI-артефакт. Локальный NuGet-кэш
                      разработчика runner не получает. Публикация выполняется
                      отдельно.
                    </p>
                  </details>
                </div>
                <div>
                  <h2>В своём окружении</h2>
                  <p>
                    Отметки — локальный checklist, не автоматическое
                    подтверждение готовности.
                  </p>
                  <div className="doc-checks deployment-checks">
                    {[
                      "HTTPS, DNS и точные callback URI зарегистрированы у Identity.",
                      "Балансировщик сохраняет Host/PathBase; доверены реальные proxy IP/CIDR.",
                      "Login с пустыми cookies → claims → защищённый API проходит полностью.",
                      "Без сессии — 401; без X-CSRF или при чужом Origin — 403.",
                      "Native JWT, scopes, Swagger и балансировка работают с BFF и без SPA.",
                      "Calendar callback проверяет state/cookie и однократность на backend.",
                      "Logout закрывает сессию; поздний refresh её не восстанавливает.",
                      "У реплик общие Redis scope и EncryptionKey; при отказе Redis — 503.",
                      "В логах/APM нет cookies, OAuth tokens и query с code/state.",
                    ].map((item) => (
                      <label key={item}>
                        <input type="checkbox" />
                        {item}
                      </label>
                    ))}
                  </div>
                  <details>
                    <summary>Локальный sample и hot reload</summary>
                    <Code
                      label="Sample host"
                      value={`# Настроить samples/.../appsettings.json и серверные секреты\ndotnet dev-certs https --trust\n# После pnpm build в ClientApp:\ndotnet run --project samples/MonixOne.OidcProxy.Yarp.SampleHost --urls https://localhost:5001`}
                    />
                    <p>
                      Sample и /docs обслуживаются из wwwroot. Для Vite задайте
                      доверенные DEV_HTTPS_CERT / DEV_HTTPS_KEY,
                      BFF_TARGET=https://localhost:5001 и запустите pnpm dev.
                      При необходимости задайте NODE_EXTRA_CA_CERTS.
                      Origin/callback/Match.Hosts согласуются с портом 5173;
                      PathBase — с Vite base и proxy paths.
                    </p>
                  </details>
                </div>
              </div>
              <section className="doc-block">
                <h2>Если что-то не работает</h2>
                <div className="table-scroll">
                  <table>
                    <thead>
                      <tr>
                        <th>Симптом</th>
                        <th>Что проверить</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr>
                        <td>404</td>
                        <td>
                          PathBase и SPA base; Match.Path после снятия префикса;
                          запущен ли API, а не только статический сервер;
                          объявлен ли профиль для /.auth.
                        </td>
                      </tr>
                      <tr>
                        <td>401</td>
                        <td>
                          BFF cookie/сессия и expiry; у native API — JWT
                          issuer/audience/signature. Повреждённый шифротекст, другой
                          ключ или неверный SessionKey тоже завершают сессию.
                        </td>
                      </tr>
                      <tr>
                        <td>403</td>
                        <td>
                          X-CSRF: 1, Origin/Sec-Fetch-Site, точный Match.Hosts,
                          scope/roles. Bearer не заменяет BFF-сессию.
                        </td>
                      </tr>
                      <tr>
                        <td>400 / 502</td>
                        <td>
                          Дубликаты cookies или неправильный Set-Cookie;
                          недоступный upstream даёт 502. Не повторять POST
                          автоматически.
                        </td>
                      </tr>
                      <tr>
                        <td>503</td>
                        <td>
                          Redis, budget refresh или неизвестный результат
                          Redis/IdP. Без memory fallback; после неудачного
                          refresh следующий запрос может дать 401.
                        </td>
                      </tr>
                      <tr>
                        <td>CORS / «сервер недоступен»</td>
                        <td>
                          HTTPS-сертификат и точный Cors:Origins. Порт является
                          частью origin. Vite-preview без GW не обслуживает BFF
                          endpoints.
                        </td>
                      </tr>
                      <tr>
                        <td>Ошибка startup</td>
                        <td>
                          PublicOrigin активного профиля, confidential
                          ClientId/ClientSecret, EncryptionKey, Redis scope и
                          отсутствие открытого legacy key ring.
                        </td>
                      </tr>
                    </tbody>
                  </table>
                </div>
              </section>
            </>
          )}
          <footer className="doc-footer">
            <span>MonixOne.OidcProxy.Yarp · LGPL-3.0-only</span>
            <span>Генератор не выполняет сетевые проверки</span>
          </footer>
        </main>
      </div>
    </div>
  );
}
