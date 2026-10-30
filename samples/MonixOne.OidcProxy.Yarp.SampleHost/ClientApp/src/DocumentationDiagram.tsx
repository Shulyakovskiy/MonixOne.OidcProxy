import { useId, useState } from "react";
import { actors, flowHops, type Actor } from "./documentationModel";

export function Architecture({ native }: { native: boolean }) {
  const [selected, setSelected] = useState<Actor>("gateway");
  const active =
    native && (selected === "identity" || selected === "redis")
      ? "gateway"
      : selected;
  const displayed = native
    ? {
        ...actors,
        browser: {
          title: "API-клиент",
          subtitle: "Native Bearer",
          detail:
            "Отправляет собственный JWT. Native policy и сервис проверяют подпись, audience, scopes и права; BFF-сессия не используется.",
        },
        gateway: {
          ...actors.gateway,
          subtitle: "YARP · JWT policies",
          detail:
            "Профили SPA отсутствуют. Пакет сохраняет native YARP, авторизацию сервисов и балансировку. Оставшийся ClientSecret без PublicOrigin не активирует BFF.",
        },
      }
    : actors;
  const node = (actor: Actor) => (
    <button
      type="button"
      className={`arch-node actor-${actor}`}
      aria-pressed={active === actor}
      onClick={() => setSelected(actor)}
    >
      <span className="arch-dot" />
      <strong>{displayed[actor].title}</strong>
      <small>{displayed[actor].subtitle}</small>
    </button>
  );
  return (
    <section
      className="architecture"
      aria-label="Интерактивная схема архитектуры"
    >
      <div className="arch-main">
        {node("browser")}
        <span className="arch-arrow">HTTPS →</span>
        {node("edge")}
        <span className="arch-arrow arch-internal">
          HTTP <span aria-hidden="true">→</span>
        </span>
        {node("gateway")}
        <span className="arch-arrow">
          {native ? "Bearer →" : "Server Bearer →"}
        </span>
        {node("api")}
      </div>
      <div className="arch-services">
        <span className="arch-service-caption">
          {native
            ? "BFF не подключён · native JWT / policies"
            : "Серверные зависимости BFF"}
        </span>
        {!native && (
          <>
            {node("identity")}
            {node("redis")}
          </>
        )}
      </div>
      <div className="diagram-detail" aria-live="polite">
        <strong>{displayed[active].title}</strong>
        <p>{displayed[active].detail}</p>
      </div>
    </section>
  );
}

export function Sequence({
  flow,
  step,
  title,
}: {
  flow: string;
  step: number;
  title: string;
}) {
  const arrow = useId().replaceAll(":", "");
  const hop = flowHops[flow]?.[step] ?? ["browser", "gateway"];
  const order: Actor[] = ["browser", "gateway", "identity", "redis", "api"];
  const from = order.indexOf(hop[0]);
  const to = order.indexOf(hop[1]);
  return (
    <div className="sequence-scroll">
      <div className="sequence">
        <div className="sequence-actors">
          {order.map((actor) => (
            <div
              key={actor}
              className={`sequence-actor actor-${actor}`}
              data-active={hop.includes(actor)}
            >
              <span className="arch-dot" />
              <strong>{actors[actor].title}</strong>
            </div>
          ))}
        </div>
        <svg
          viewBox="0 0 1000 104"
          role="img"
          aria-label={`${actors[hop[0]].title} → ${actors[hop[1]].title}: ${title}`}
        >
          <title>{title}</title>
          <desc>
            Выберите этап ниже, чтобы увидеть направление сообщения и проверки
            на сервере.
          </desc>
          <defs>
            <marker
              id={arrow}
              markerWidth="8"
              markerHeight="8"
              refX="7"
              refY="4"
              orient="auto"
            >
              <path d="M0,0 L8,4 L0,8" fill="currentColor" />
            </marker>
          </defs>
          {order.map((actor, index) => (
            <line
              key={actor}
              x1={100 + index * 200}
              x2={100 + index * 200}
              y1="0"
              y2="104"
              className="sequence-lifeline"
            />
          ))}
          <line
            x1={100 + from * 200 + (to > from ? 8 : -8)}
            x2={100 + to * 200 + (to > from ? -15 : 15)}
            y1="55"
            y2="55"
            className="sequence-message"
            markerEnd={`url(#${arrow})`}
          />
          <circle
            cx={100 + from * 200}
            cy="55"
            r="6"
            className="sequence-origin"
          />
          <text
            x={(200 + (from + to) * 200) / 2}
            y="30"
            textAnchor="middle"
            className="sequence-label"
          >
            Этап {step + 1}
          </text>
        </svg>
        <div className="sequence-caption">{title}</div>
      </div>
    </div>
  );
}
