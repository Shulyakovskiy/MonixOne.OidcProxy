import { useState } from "react";
import { createConfiguration, flows, hostCode, initialSetup, createSpaCode, validateClients, type Setup, type ClientSetup } from "./documentationContent";
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
  return <section className="doc-code" aria-label={label}>
    <div><strong>{label}</strong><button type="button" onClick={copy}>{copiedValue === value ? "Скопировано" : "Копировать"}</button></div>
    <pre><code>{value}</code></pre>
    {error && <p role="status">Буфер обмена недоступен. Выделите код и скопируйте вручную.</p>}
  </section>;
}

const steps = ["Топология", "OpenID Provider", "Redis и ключи", "Хост и YARP", "React SPA", "Проверка"];
const fields: [keyof Setup, string][] = [
  ["name", "Client name"], ["origin", "SPA / BFF origin"], ["pathBase", "PathBase (пусто для корня)"], ["authority", "OIDC Authority"], ["clientId", "Client ID"],
  ["api", "Resource API"], ["application", "Application name"], ["environment", "Environment"],
  ["namespace", "Redis namespace"], ["scopes", "Scopes"],
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
  const callback = valid ? new URL(targetRoutes.loginCallback, setup.origin).href : "Исправьте URL на шаге 1";
  const logoutCallback = valid ? new URL(targetRoutes.logoutCallback, setup.origin).href : "Исправьте URL на шаге 1";

  return <div className="docs">
    <header className="doc-header">
      <a href={routes.docs} className="doc-brand"><img src={`${routes.docsAssets}/nuget-icon.png`} alt="" width="40" height="40" />MonixOne.OidcProxy.Yarp</a>
      <a href="https://github.com/Shulyakovskiy/MonixOne.OidcProxy.Yarp">Исходный код ↗</a>
    </header>
    <main>
      <div className="doc-hero"><p className="doc-kicker">.NET 10 · YARP · OPENID CONNECT · REDIS</p>
        <h1>SPA с серверной<br /><span>OAuth-сессией.</span></h1>
        <p>Настройте BFF и разберите путь каждого запроса. В браузере — UI и HttpOnly cookie. Токены, refresh и confidential credentials остаются на сервере.</p>
      </div>
      <nav className="doc-tabs" aria-label="Раздел документации">
        <button aria-pressed={section === "setup"} onClick={() => setSection("setup")}>01 · Пошаговая настройка</button>
        <button aria-pressed={section === "flows"} onClick={() => setSection("flows")}>02 · Как работает SPA</button>
      </nav>
      {section === "setup" ? <div className="doc-layout">
        <nav className="doc-steps" aria-label="Шаги настройки">
          {steps.map((title, index) => <button key={title} aria-current={step === index ? "step" : undefined} onClick={() => setStep(index)}><span>{String(index + 1).padStart(2, "0")}</span>{title}</button>)}
          <p>Параметры обрабатываются только в этой вкладке, не сохраняются и не отправляются на сервер. Секреты сюда вводить не нужно.</p>
        </nav>
        <article className="doc-panel">
          <p className="doc-kicker">ШАГ {step + 1} ИЗ {steps.length}</p><h2>{steps[step]}</h2>
          {step === 0 && <>
            <p>Один BFF/YARP-хост обслуживает несколько SPA на разных HTTPS origins. Для каждой SPA задаются свой client name, OIDC client_id, callback URI, cookie-схема и Redis sessions. Resource API и OpenID Provider — отдельные серверы. Пакет проксирует только маршруты из конфигурации YARP.</p>
            <div className="doc-field-grid">{fields.map(([key, label]) => <label key={key}>{label}<input value={setup[key]} autoComplete="off" spellCheck={false} onChange={e => setSetup({ ...setup, [key]: e.target.value })} /></label>)}</div>
            {others.map((client, index) => <fieldset key={index}><legend>SPA {index + 2}</legend>
              <div className="doc-field-grid">{fields.filter(([key]) => !["application", "environment", "namespace"].includes(key)).map(([key, label]) => {
                const clientKey = key as keyof ClientSetup;
                return <label key={key}>SPA {index + 2} · {label}<input value={client[clientKey]} autoComplete="off" spellCheck={false} onChange={e => setOthers(others.map((value, at) => at === index ? { ...value, [key]: e.target.value } : value))} /></label>;
              })}</div><button type="button" onClick={() => setOthers(others.filter((_, at) => at !== index))}>Удалить SPA {index + 2}</button>
            </fieldset>)}
            <button type="button" onClick={() => setOthers([...others, { ...initialSetup, name: `spa${others.length + 2}`, origin: `https://spa${others.length + 2}.example.com`, clientId: `spa${others.length + 2}-bff` }])}>Добавить SPA</button>
            {!valid && <div role="alert" className="doc-note doc-error"><strong>Проверьте параметры</strong><ul>{errors.map(error => <li key={error}>{error}</li>)}</ul></div>}
            <p className="doc-note">Это проверка формата, а не проверка подключения. DNS, TLS, доступность Redis, регистрация клиента и права на API проверяются при запуске и ручном проходе сценария.</p>
            <img className="doc-diagram" src={`${routes.docsAssets}/BFF-flow.png`} alt="SPA обращается к BFF через cookie и CSRF header; BFF использует Redis, OpenID Provider и YARP для доступа к API." />
          </>}
          {step === 1 && <>
            <p>Зарегистрируйте <strong>confidential web client</strong>. Разрешите Authorization Code, Refresh Token и PKCE S256. SPA не регистрируется как public OAuth client и не получает client secret.</p>
            <Code label="Redirect URI" value={callback} /><Code label="Post logout redirect URI" value={logoutCallback} />
            {valid && others.map(client => <section key={client.name}><h3>{client.name} · {client.origin}</h3>
              <Code label={`Redirect URI · ${client.name}`} value={new URL(createRoutes(client.pathBase).loginCallback, client.origin).href} />
              <Code label={`Post logout redirect URI · ${client.name}`} value={new URL(createRoutes(client.pathBase).logoutCallback, client.origin).href} />
            </section>)}
            <p>Зарегистрируйте каждого клиента отдельно, даже если используется один OpenID Provider. Выдайте клиенту нужные scopes. <code>openid</code> включает OIDC, <code>profile</code> — профиль, <code>offline_access</code> — право запрашивать refresh token. API scopes и resource/audience определяет ваш провайдер.</p>
            <details open><summary>OpenIddict: что должно быть настроено</summary><ul>
              <li>Стандартные discovery/JWKS, authorization, token, end-session и revocation endpoints по HTTPS.</li>
              <li>У client application — secret, точные RedirectUris и PostLogoutRedirectUris; permissions для authorization, token, end-session, revocation, code и refresh grants, response type code и требуемых scopes.</li>
              <li>На стороне сервера — <code>AllowAuthorizationCodeFlow()</code>, <code>AllowRefreshTokenFlow()</code> и PKCE. Для production используйте постоянные signing/encryption credentials.</li>
              <li>Приложение провайдера отвечает за login/consent UI, refresh principal и разрешённые resources/scopes. Пакет не заменяет реализацию authorization server.</li>
              <li>Encrypted access tokens OpenIddict поддерживаются: BFF передаёт их как opaque credentials. Resource API должен настроить официальную OpenIddict validation/decryption либо допустимую introspection-модель.</li>
              <li>Claims в ID token управляют профилем SPA; access token и API validation определяют полномочия на ресурсы.</li>
            </ul><a href="https://documentation.openiddict.com/guides/">Официальные руководства OpenIddict ↗</a></details>
            <p>Провайдер должен возвращать signed ID token и access token типа Bearer с положительным expires_in. Для завершения SSO-сессии необходим RP-initiated logout. Если revocation endpoint отсутствует, локальный logout работает, отзыв refresh token недоступен.</p>
          </>}
          {step === 2 && <>
            <p>Зарегистрируйте один host-owned <code>ConnectionMultiplexer</code>. Все реплики одного BFF используют одинаковый Redis и одинаковые application/environment/namespace. Независимым проектам и окружениям задайте разные значения.</p>
            <Code label="Префикс Redis" value={`${setup.application.toLowerCase()}:${setup.environment.toLowerCase()}:${setup.namespace.toLowerCase()}:${setup.name}:sessions:`} />
            <p>Пакет использует Redis для защищённых session tickets и общего Data Protection key ring. Поддерживаются Redis 6.2+ команды GETDEL и Lua CAS. Memory fallback отсутствует: потеря Redis приводит к отказу авторизации.</p>
            <Code label="Настройки через secret provider / environment" value={`ASPNETCORE_ENVIRONMENT=${setup.environment}\nConnectionStrings__Redis=<set through your secret provider>\n${[setup, ...others].map(client => `Bff__Clients__${client.name}__OpenIdConnect__ClientSecret=<set through your secret provider>`).join("\n")}`} />
            <p className="doc-note">Не помещайте секреты в VITE_* или bundle SPA. Redis требует TLS, аутентификацию, ACL, контролируемую сеть и выбранную политику persistence/backup. Для server-session keys не применяйте произвольное eviction: потерянная запись завершит сессию.</p>
            <details><summary>Data Protection и восстановление Redis</summary><p>Шифрование ticket не защищает от полного доступа к Redis, если в нём же хранится незащищённый master key ring. Хост должен добавить стандартную защиту Data Protection keys независимым сертификатом или KMS, например через <code>AddDataProtection().ProtectKeysWithCertificate(certificate)</code>. Все реплики должны уметь расшифровывать один key ring.</p><p>При восстановлении backup учитывайте, что он может вернуть удалённые сессии и старые refresh credentials. План восстановления должен сбрасывать сессии и отзывать grants или менять область ключей; пакет сам не может обеспечить монотонность внешнего backup.</p></details>
          </>}
          {step === 3 && <>
            <Code label="Установка" value="dotnet pack src/MonixOne.OidcProxy.Yarp/MonixOne.OidcProxy.Yarp.csproj -c Release -o artifacts/nuget" />
            <p>Пакет ещё не опубликован в NuGet. Сейчас используются ProjectReference из репозитория или локально собранный nupkg. Добавляйте YARP и ConnectionMultiplexer перед AddOidcProxy. Передайте Bff configuration section одним вызовом: пакет сам обходит Clients, регистрирует именованные OIDC/cookie-схемы и использует окружение хоста. Конфигурация OIDC — стандартный OpenIdConnectOptions.</p>
            {valid ? <Code label="appsettings.json — без секретов" value={createConfiguration(setup, others)} /> : <p role="alert" className="doc-note doc-error">Сначала исправьте параметры на шаге «Топология».</p>}
            <Code label="Program.cs" value={hostCode} />
            <p>Пример передаёт путь <code>/api/…</code> без преобразования. YARP Match.Path может описывать любые API пути, а transforms — менять их перед отправкой. SPA fallback <code>{"{**path}"}</code> обрабатывает все остальные GET/HEAD пути, включая сегменты с точками. Для размещения SPA под путём задайте PathBase её клиенту, соберите Vite с тем же base и сохраните префикс на ingress. Каждый BFF route связывается с именем клиента через Metadata["OidcProxy.Client"] и точный Match.Hosts. Защищённый route требует сессию выбранного клиента и CSRF header. Явный AuthorizationPolicy="anonymous" подходит для public assets и не передаёт токены. Маршруты других хостов без этой metadata сохраняют нативное поведение YARP; маршрут на SPA-хосте без привязки отклоняется. Разные SPA bundles обычно обслуживают отдельные anonymous YARP routes; sample показывает один локальный bundle.</p>
            <details><summary>TLS termination и ingress</summary><p>PublicOrigin — внешний адрес браузера. Если TLS завершается на ingress, настройте стандартный ForwardedHeaders middleware перед HSTS/HTTPS redirection и BFF. Доверяйте только конкретным KnownProxies/KnownNetworks. Не доверяйте произвольному входящему X-Forwarded-*; не отключайте TLS validation downstream.</p><p>Убедитесь, что SPA, callback URI и API-запросы видны браузеру на одном origin. Для Vite development используйте HTTPS dev proxy; PublicOrigin должен совпадать с адресом Vite.</p></details>
          </>}
          {step === 4 && <>
            <p>Устанавливайте <code>X-CSRF: 1</code> для всех BFF data endpoints и проксируемых API, включая GET. Не добавляйте cross-origin CORS-доступ к session API. Переход на login и logout выполняйте навигацией браузера. Стандартный returnUrl принимает локальный маршрут с query/fragment; внешние URL и повторяющиеся параметры отклоняются до начала login/logout.</p>
            <Code label="Login и чтение сессии" value={createSpaCode(setup.pathBase)} />
            <p>Sample использует официальный <code>@tanstack/react-query</code>, а не OAuth SDK для public SPA. В <code>src/session.ts</code> заданы focus/reconnect refetch, проверка каждые 60 секунд только при активной authenticated SPA и отключение автоматических retries. В фоне interval не работает; absolute session lifetime ограничен сервером.</p>
            <Code label="Native POST logout с framework antiforgery token" value={`const response = await fetch(${JSON.stringify(targetRoutes.antiforgery)}, {\n  credentials: "same-origin", headers: { "X-CSRF": "1" },\n});\nif (!response.ok) throw new Error(\`BFF returned \${response.status}\`);\nconst { requestToken, formFieldName } = await response.json();\nconst form = document.createElement("form");\nform.method = "POST";\nform.action = ${JSON.stringify(targetRoutes.logout)};\nconst field = document.createElement("input");\nfield.type = "hidden";\nfield.name = formFieldName;\nfield.value = requestToken;\nform.append(field);\ndocument.body.append(form);\nform.submit();`} />
            <p>Перед logout отменяйте текущие query и блокируйте новые UI-запросы. После 401 очищайте кэш пользователя и API. После 503 показывайте недоступность, без автоматического повтора POST/refresh или перенаправления в бесконечный login.</p>
            <p className="doc-note">HttpOnly скрывает cookie от JavaScript, но XSS всё ещё может выполнять запросы от имени пользователя. Хост SPA отвечает за CSP, безопасный DOM, зависимости, входные данные и отсутствие секретов в bundle/logs.</p>
          </>}
          {step === 5 && <>
            <p>Проверки ниже выполняет оператор на своём окружении. Эта страница не подключается к вашим серверам и не подтверждает работоспособность конфигурации.</p>
            <div className="doc-checklist">{[
              "Discovery/JWKS доступны BFF по HTTPS; провайдер принимает confidential client и callback URI.",
              "Login возвращает на /; cookie имеет Secure, HttpOnly, SameSite=Strict, Path=/ и не содержит OAuth tokens.",
              "GET /.auth/me с X-CSRF: 1 возвращает claims; без сессии — 401, без header — 403.",
              "API принимает server access token и проверяет audience, scope/roles; входящий browser Bearer не проходит как BFF session.",
              "По истечении access token параллельные запросы через две реплики вызывают один refresh.",
              "Logout во время refresh удаляет запись; поздний refresh не возвращает авторизованную сессию.",
              "После logout старые cookie дают 401; UI очищает данные пользователя.",
              "Два SPA-хоста используют разные client_id/cookies; перенесённые cookie/state не авторизуют другого клиента. Logout и refresh локально изолированы; проверьте также глобальную SSO-политику IdP.",
              "При недоступности Redis BFF возвращает 503; токены, cookies, codes и client secret не попадают в логи.",
              "Ingress доверяет только настроенным proxies; DP key ring защищён независимым сертификатом/KMS.",
            ].map(item => <label key={item}><input type="checkbox" />{item}</label>)}</div>
            <Code label="Проверки репозитория — нужны .NET 10, Docker, Node 24+, pnpm" value={`dotnet test --project tests/MonixOne.OidcProxy.Yarp.Tests/MonixOne.OidcProxy.Yarp.Tests.csproj -c Release -p:GeneratePackageOnBuild=false\ncd samples/MonixOne.OidcProxy.Yarp.SampleHost/ClientApp\ncorepack pnpm install --frozen-lockfile\ncorepack pnpm test\ncorepack pnpm build`} />
            <p>Автотесты проходят реальный HTTP pipeline sample через WebApplicationFactory, WireMock OIDC и Redis. Отдельные сценарии используют настоящий OpenIddict с PostgreSQL и HTTPS API, в том числе encrypted access tokens.</p>
          </>}
          <div className="doc-pagination"><button className="secondary" disabled={step === 0} onClick={() => setStep(step - 1)}>← Назад</button><span>{step + 1} / {steps.length}</span>{step < steps.length - 1 ? <button onClick={() => setStep(step + 1)}>Далее →</button> : <button onClick={() => setSection("flows")}>Как работает SPA →</button>}</div>
        </article>
      </div> : <article className="doc-panel doc-explainer">
        <p className="doc-kicker">ЖИЗНЕННЫЙ ЦИКЛ СЕССИИ</p><h2>От первого входа до logout</h2>
        <div className="doc-flow-tabs" aria-label="Сценарии SPA">{(Object.keys(flows) as (keyof typeof flows)[]).map(key => <button key={key} aria-pressed={flow === key} onClick={() => { setFlow(key); setFlowStep(0); }}>{flows[key].title}</button>)}</div>
        <h3>{current.title}</h3><p>{current.summary}</p>
        <div className="doc-flow-layout"><nav className="doc-flow-steps" aria-label="Этапы сценария">{current.steps.map(([title], index) => <button key={title} aria-current={flowStep === index ? "step" : undefined} onClick={() => setFlowStep(index)}><span>{index + 1}</span>{title}</button>)}</nav>
          <div className="doc-flow-detail" aria-live="polite"><p className="doc-kicker">ЭТАП {flowStep + 1} / {current.steps.length}</p><h3>{detail[0]}</h3><p>{detail[1]}</p><p className="doc-note">{detail[2]}</p><div className="doc-pagination"><button className="secondary" disabled={flowStep === 0} onClick={() => setFlowStep(flowStep - 1)}>Предыдущий этап</button><button disabled={flowStep === current.steps.length - 1} onClick={() => setFlowStep(flowStep + 1)}>Следующий этап</button></div></div>
        </div>
        <details><summary>Полная схема {flow === "login" ? "входа" : "взаимодействия"}</summary><a href={flow === "login" ? `${routes.docsAssets}/BFF-login-workflow.png` : `${routes.docsAssets}/BFF-flow.png`}><img className="doc-diagram" src={flow === "login" ? `${routes.docsAssets}/BFF-login-workflow.png` : `${routes.docsAssets}/BFF-flow.png`} alt="Полная схема SPA BFF; подробное текстовое описание приведено в этапах выше." /></a></details>
        <h3>Контракт браузера</h3><p>Пути ниже относятся к текущему Vite base. Client PathBase и Vite base должны совпадать; Callback URI также включают PathBase.</p><div className="doc-table-wrap"><table><thead><tr><th>Endpoint</th><th>Как вызвать</th><th>Результат</th></tr></thead><tbody>
          <tr><td>GET {routes.login}</td><td>Top-level navigation</td><td>OIDC redirect, затем /</td></tr>
          <tr><td>GET {routes.user}</td><td>Cookie + X-CSRF: 1</td><td>Claims; 401 без сессии</td></tr>
          <tr><td>/api/…</td><td>Cookie + X-CSRF: 1</td><td>YARP → настроенный HTTPS API</td></tr>
          <tr><td>GET {routes.antiforgery}</td><td>Cookie + X-CSRF: 1</td><td>Framework request token для logout form</td></tr>
          <tr><td>POST {routes.logout}</td><td>Native form + antiforgery; либо X-CSRF: 1</td><td>Удаление сессии, redirect к provider</td></tr>
          <tr><td>GET /.auth/login/callback</td><td>Только ответ провайдера со state/code</td><td>Обмен code, новая cookie</td></tr>
          <tr><td>GET /.auth/signout-callback</td><td>Ответ провайдера при logout</td><td>Завершение sign-out → /</td></tr>
        </tbody></table></div>
        <div className="doc-responsibilities"><section><h3>Браузер / React</h3><p>UI, query cache, явный login, same-origin fetch, обработка 401/403/503. Токенами не управляет.</p></section><section><h3>BFF / Redis / YARP</h3><p>OIDC client, server ticket, CSRF boundary, сериализация refresh, локальный logout, фиксированные API destinations.</p></section><section><h3>Provider / Resource API</h3><p>Учётные данные пользователя, grants и token issuance; в API — token validation и авторизация по ресурсам.</p></section></div>
        <h3>Границы гарантий</h3><p>Logout запрещает новые запросы с удалённой локальной сессией. Он не отзывает уже отправленный HTTP-запрос и не делает Redis и провайдера общей транзакцией. Recovery из старого Redis backup, компрометация ключей и XSS требуют мер на стороне хоста и инфраструктуры.</p>
      </article>}
      <aside className="doc-sources"><h2>Стандарты и первичные источники</h2><a href="https://datatracker.ietf.org/doc/html/rfc10017">OAuth 2.0 for Browser-Based Applications</a><a href="https://www.rfc-editor.org/rfc/rfc9700.html">OAuth Security BCP</a><a href="https://openid.net/specs/openid-connect-rpinitiated-1_0.html">OIDC RP-Initiated Logout</a><a href="https://documentation.openiddict.com/">OpenIddict</a><a href="https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/getting-started?view=aspnetcore-10.0">YARP</a><a href="https://tanstack.com/query/latest/docs/framework/react/guides/window-focus-refetching">TanStack Query</a></aside>
    </main>
    <footer className="doc-footer"><span>MonixOne.OidcProxy.Yarp · LGPL-3.0-only</span><span>Статическая документация · секреты не запрашиваются</span></footer>
  </div>;
}
