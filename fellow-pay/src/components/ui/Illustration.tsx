"use client";

import React from "react";

/**
 * Sistema centralizado de illustrations vetoriais. Componente único com map
 * de `name → SVG` — caller usa `<Illustration name="empty-catalog" />`.
 *
 * Estilo: geométrico, flat, gradient brand-purple, formas grandes. Inspirado
 * no padrão Flowbite/undraw.co. Cada illustration:
 *  - Renderiza em SVG inline (zero HTTP request adicional, edita por currentColor).
 *  - Suporta dark mode via `text-brand-500 dark:text-brand-400` no wrapper —
 *    o gradient e os tons puxam dessas classes.
 *  - Aceita `size="sm" | "md" | "lg"` para escalar (controla apenas o wrapper;
 *    o SVG mantém aspect-ratio via viewBox).
 *  - Aceita `className` para overrides finos (cor primária, margens etc).
 *
 * Para adicionar uma nova illustration:
 *  1. Defina o SVG no map `ILLUSTRATIONS` abaixo (viewBox preferencialmente
 *     0 0 320 240 — proporção 4:3 — ou 0 0 240 240 quadrado).
 *  2. Use `currentColor` na cor primária e literals brand-{tone} para
 *     acentos que não devem trocar com a prop `className`.
 *  3. Adicione o name ao union type `IllustrationName`.
 */

export type IllustrationName =
  | "empty-catalog"
  | "integrations-empty"
  | "no-results"
  | "payment-success"
  | "not-found-404"
  | "server-error"
  | "welcome";

const SIZE_MAP: Record<"sm" | "md" | "lg" | "xl", string> = {
  sm: "w-32 h-24",
  md: "w-48 h-36",
  lg: "w-64 h-48",
  xl: "w-80 h-60",
};

export function Illustration({
  name,
  size = "md",
  className = "",
}: {
  name: IllustrationName;
  size?: "sm" | "md" | "lg" | "xl";
  className?: string;
}) {
  const Svg = ILLUSTRATIONS[name];
  return (
    <div
      className={`${SIZE_MAP[size]} text-brand-500 dark:text-brand-400 ${className}`}
      aria-hidden="true"
    >
      <Svg />
    </div>
  );
}

/* ---------------------------------------------------------------- SVGs ---- */

/**
 * Empty catalog — caixa aberta com produtos flutuando ao redor. Comunica
 * "ainda não há produtos aqui" sem tom negativo. Acento brand-500 na caixa,
 * shapes secundários em brand-100/200 (light) e brand-500/30 (dark).
 */
function EmptyCatalog() {
  return (
    <svg
      viewBox="0 0 320 240"
      fill="none"
      className="w-full h-full"
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* Background ground shadow */}
      <ellipse
        cx="160"
        cy="210"
        rx="110"
        ry="10"
        className="fill-brand-100/60 dark:fill-brand-500/10"
      />
      {/* Floating decorative shapes */}
      <rect
        x="40"
        y="55"
        width="20"
        height="20"
        rx="4"
        className="fill-brand-200/70 dark:fill-brand-500/20"
        transform="rotate(15 50 65)"
      />
      <circle
        cx="270"
        cy="70"
        r="8"
        className="fill-brand-300/70 dark:fill-brand-500/30"
      />
      <rect
        x="265"
        y="160"
        width="14"
        height="14"
        rx="3"
        className="fill-brand-200/70 dark:fill-brand-500/20"
        transform="rotate(-20 272 167)"
      />
      <circle
        cx="55"
        cy="180"
        r="6"
        className="fill-brand-300/70 dark:fill-brand-500/30"
      />
      {/* Box body */}
      <path
        d="M100 120 L160 105 L220 120 L220 195 L160 210 L100 195 Z"
        className="fill-brand-100 dark:fill-brand-500/20 stroke-current"
        strokeWidth="2"
        strokeLinejoin="round"
      />
      {/* Box top flaps (open) */}
      <path
        d="M100 120 L130 95 L190 95 L160 105 Z"
        className="fill-brand-200 dark:fill-brand-500/30 stroke-current"
        strokeWidth="2"
        strokeLinejoin="round"
      />
      <path
        d="M160 105 L190 95 L250 95 L220 120 Z"
        className="fill-brand-300 dark:fill-brand-500/40 stroke-current"
        strokeWidth="2"
        strokeLinejoin="round"
      />
      {/* Inside shadow */}
      <path
        d="M130 113 L160 122 L190 113"
        className="stroke-current opacity-40"
        strokeWidth="2"
        strokeLinecap="round"
      />
      {/* Box label (current) */}
      <rect
        x="138"
        y="155"
        width="44"
        height="18"
        rx="3"
        className="fill-white dark:fill-gray-900 stroke-current"
        strokeWidth="2"
      />
      <line
        x1="146"
        y1="162"
        x2="174"
        y2="162"
        className="stroke-current opacity-50"
        strokeWidth="2"
        strokeLinecap="round"
      />
      <line
        x1="146"
        y1="167"
        x2="166"
        y2="167"
        className="stroke-current opacity-30"
        strokeWidth="2"
        strokeLinecap="round"
      />
    </svg>
  );
}

/**
 * Integrations empty — duas pessoas conectando plugues. Para telas de
 * webhook/integração sem registros, comunica conexão entre sistemas.
 */
function IntegrationsEmpty() {
  return (
    <svg
      viewBox="0 0 320 240"
      fill="none"
      className="w-full h-full"
      xmlns="http://www.w3.org/2000/svg"
    >
      <ellipse
        cx="160"
        cy="216"
        rx="110"
        ry="10"
        className="fill-brand-100/60 dark:fill-brand-500/10"
      />

      {/* Cables */}
      <path
        d="M69 176 C42 176 38 155 42 131 C45 111 41 96 20 96"
        className="stroke-gray-300 dark:stroke-gray-700"
        strokeWidth="5"
        strokeLinecap="round"
      />
      <path
        d="M251 176 C278 176 282 155 278 131 C275 111 279 96 300 96"
        className="stroke-gray-300 dark:stroke-gray-700"
        strokeWidth="5"
        strokeLinecap="round"
      />

      {/* Left person */}
      <path
        d="M92 78 C93 67 100 61 111 64 C120 66 125 75 123 85 C121 95 113 101 103 99 C95 97 91 89 92 78Z"
        className="fill-gray-800 dark:fill-gray-300"
      />
      <path
        d="M101 88 C101 99 94 104 91 115 L128 115 C126 104 120 98 119 88 C115 94 105 95 101 88Z"
        className="fill-brand-100 dark:fill-brand-500/20"
      />
      <path
        d="M91 115 L84 201 H126 L119 115Z"
        className="fill-brand-500"
      />
      <path
        d="M107 115 L101 201 H126 L119 115Z"
        className="fill-brand-600/80"
      />
      <path
        d="M84 201 H104 L100 219 H78 L84 201Z"
        className="fill-brand-100 stroke-brand-500/50 dark:fill-brand-500/20"
        strokeWidth="1.5"
      />
      <path
        d="M106 201 H126 L138 219 H113 L106 201Z"
        className="fill-brand-100 stroke-brand-500/50 dark:fill-brand-500/20"
        strokeWidth="1.5"
      />
      <path
        d="M83 126 C74 142 82 164 104 176"
        className="stroke-warning-400"
        strokeWidth="7"
        strokeLinecap="round"
      />
      <path
        d="M134 126 C143 144 138 164 121 176"
        className="stroke-warning-400"
        strokeWidth="7"
        strokeLinecap="round"
      />
      <path
        d="M86 106 C90 94 99 89 111 89 C124 89 132 96 136 108 L127 124 H94 L86 106Z"
        className="fill-white dark:fill-gray-100"
      />

      {/* Left plug */}
      <g className="drop-shadow-sm">
        <path
          d="M126 103 C145 101 161 113 166 132 C171 151 160 168 141 172 L124 176 L108 110 L126 103Z"
          className="fill-brand-100 stroke-brand-300 dark:fill-brand-500/25 dark:stroke-brand-400/70"
          strokeWidth="2"
        />
        <path
          d="M124 119 L151 112"
          className="stroke-brand-200 dark:stroke-brand-400/50"
          strokeWidth="8"
          strokeLinecap="round"
        />
        <path
          d="M130 144 L157 137"
          className="stroke-brand-200 dark:stroke-brand-400/50"
          strokeWidth="8"
          strokeLinecap="round"
        />
        <path
          d="M164 133 H177"
          className="stroke-brand-300 dark:stroke-brand-400"
          strokeWidth="9"
          strokeLinecap="round"
        />
      </g>

      {/* Right person */}
      <path
        d="M225 74 C224 64 231 58 241 61 C250 64 254 73 250 83 C247 93 238 97 229 94 C223 91 221 82 225 74Z"
        className="fill-gray-800 dark:fill-gray-300"
      />
      <path
        d="M228 112 C226 135 220 172 214 201 H257 C250 172 247 135 248 112Z"
        className="fill-brand-500"
      />
      <path
        d="M241 112 C240 137 239 174 235 201 H257 C250 172 247 135 248 112Z"
        className="fill-brand-600/80"
      />
      <path
        d="M216 201 H235 L228 218 H204 L216 201Z"
        className="fill-gray-100 stroke-gray-400 dark:fill-gray-700"
        strokeWidth="1.5"
      />
      <path
        d="M238 201 H257 L270 218 H247 L238 201Z"
        className="fill-gray-100 stroke-gray-400 dark:fill-gray-700"
        strokeWidth="1.5"
      />
      <path
        d="M222 126 C212 146 215 165 231 176"
        className="stroke-warning-400"
        strokeWidth="7"
        strokeLinecap="round"
      />
      <path
        d="M256 125 C263 145 258 164 240 176"
        className="stroke-warning-400"
        strokeWidth="7"
        strokeLinecap="round"
      />
      <path
        d="M219 103 C224 93 232 88 243 88 C255 88 262 96 265 108 L259 124 H224 L219 103Z"
        className="fill-white dark:fill-gray-100"
      />
      <g className="opacity-60">
        <circle cx="232" cy="101" r="2" className="fill-brand-200" />
        <circle cx="246" cy="104" r="2" className="fill-brand-200" />
        <circle cx="239" cy="114" r="2" className="fill-brand-200" />
      </g>

      {/* Right plug */}
      <g className="drop-shadow-sm">
        <path
          d="M194 105 C176 105 162 118 160 137 C157 156 170 171 189 173 H209 L211 105H194Z"
          className="fill-brand-100 stroke-brand-300 dark:fill-brand-500/25 dark:stroke-brand-400/70"
          strokeWidth="2"
        />
        <path
          d="M194 105 C204 121 204 158 189 173"
          className="stroke-brand-200 dark:stroke-brand-400/50"
          strokeWidth="8"
          strokeLinecap="round"
        />
        <path
          d="M159 137 H146"
          className="stroke-brand-300 dark:stroke-brand-400"
          strokeWidth="9"
          strokeLinecap="round"
        />
        <path
          d="M211 121 H226"
          className="stroke-brand-200 dark:stroke-brand-400/60"
          strokeWidth="8"
          strokeLinecap="round"
        />
        <path
          d="M211 157 H226"
          className="stroke-brand-200 dark:stroke-brand-400/60"
          strokeWidth="8"
          strokeLinecap="round"
        />
      </g>
    </svg>
  );
}

/**
 * No results — lupa sobre um documento vazio. Filtra/busca não retornou nada.
 */
function NoResults() {
  return (
    <svg
      viewBox="0 0 320 240"
      fill="none"
      className="w-full h-full"
      xmlns="http://www.w3.org/2000/svg"
    >
      <ellipse
        cx="160"
        cy="215"
        rx="100"
        ry="9"
        className="fill-brand-100/60 dark:fill-brand-500/10"
      />
      {/* Document */}
      <rect
        x="105"
        y="55"
        width="110"
        height="140"
        rx="6"
        className="fill-white dark:fill-gray-800 stroke-current"
        strokeWidth="2"
      />
      <line
        x1="120"
        y1="80"
        x2="200"
        y2="80"
        className="stroke-current opacity-30"
        strokeWidth="3"
        strokeLinecap="round"
      />
      <line
        x1="120"
        y1="95"
        x2="180"
        y2="95"
        className="stroke-current opacity-20"
        strokeWidth="3"
        strokeLinecap="round"
      />
      <line
        x1="120"
        y1="110"
        x2="195"
        y2="110"
        className="stroke-current opacity-20"
        strokeWidth="3"
        strokeLinecap="round"
      />
      {/* Magnifying glass */}
      <circle
        cx="180"
        cy="145"
        r="36"
        className="fill-brand-50 dark:fill-brand-500/10 stroke-current"
        strokeWidth="4"
      />
      <line
        x1="206"
        y1="171"
        x2="232"
        y2="197"
        className="stroke-current"
        strokeWidth="6"
        strokeLinecap="round"
      />
      <line
        x1="206"
        y1="171"
        x2="232"
        y2="197"
        className="stroke-brand-700 dark:stroke-brand-300 opacity-50"
        strokeWidth="2"
        strokeLinecap="round"
      />
      {/* Question mark inside lens */}
      <text
        x="180"
        y="158"
        textAnchor="middle"
        className="fill-current"
        fontSize="36"
        fontWeight="700"
        fontFamily="system-ui, -apple-system, sans-serif"
      >
        ?
      </text>
      {/* Floating accents */}
      <circle
        cx="60"
        cy="80"
        r="5"
        className="fill-brand-300/70 dark:fill-brand-500/30"
      />
      <circle
        cx="265"
        cy="60"
        r="7"
        className="fill-brand-200/70 dark:fill-brand-500/20"
      />
      <rect
        x="50"
        y="180"
        width="12"
        height="12"
        rx="2"
        className="fill-brand-200/70 dark:fill-brand-500/20"
        transform="rotate(20 56 186)"
      />
    </svg>
  );
}

/**
 * Payment success — confetti + check mark grande. Momento emocional do
 * checkout, comunica "deu certo" sem ser exagerado.
 */
function PaymentSuccess() {
  return (
    <svg
      viewBox="0 0 320 240"
      fill="none"
      className="w-full h-full"
      xmlns="http://www.w3.org/2000/svg"
    >
      <ellipse
        cx="160"
        cy="215"
        rx="100"
        ry="9"
        className="fill-success-100/70 dark:fill-success-500/10"
      />
      {/* Outer success circle */}
      <circle
        cx="160"
        cy="115"
        r="68"
        className="fill-success-100 dark:fill-success-500/20"
      />
      {/* Inner ring */}
      <circle
        cx="160"
        cy="115"
        r="52"
        className="fill-success-200 dark:fill-success-500/30 stroke-success-500"
        strokeWidth="3"
      />
      {/* Check */}
      <polyline
        points="135,118 154,137 188,98"
        className="stroke-success-600 dark:stroke-success-400"
        strokeWidth="8"
        strokeLinecap="round"
        strokeLinejoin="round"
        fill="none"
      />
      {/* Confetti */}
      <rect
        x="50"
        y="60"
        width="10"
        height="10"
        rx="2"
        className="fill-brand-400"
        transform="rotate(20 55 65)"
      />
      <circle cx="265" cy="55" r="6" className="fill-success-400" />
      <rect
        x="70"
        y="180"
        width="8"
        height="8"
        rx="2"
        className="fill-success-400"
        transform="rotate(-15 74 184)"
      />
      <circle cx="40" cy="140" r="5" className="fill-brand-300" />
      <rect
        x="255"
        y="160"
        width="12"
        height="12"
        rx="2"
        className="fill-brand-400"
        transform="rotate(35 261 166)"
      />
      <circle cx="280" cy="125" r="4" className="fill-success-500" />
      {/* Sparkles */}
      <path
        d="M30 30 L34 30 M32 28 L32 32"
        className="stroke-brand-400"
        strokeWidth="2"
        strokeLinecap="round"
      />
      <path
        d="M290 90 L296 90 M293 87 L293 93"
        className="stroke-success-500"
        strokeWidth="2"
        strokeLinecap="round"
      />
      <path
        d="M250 30 L254 30 M252 28 L252 32"
        className="stroke-brand-300"
        strokeWidth="2"
        strokeLinecap="round"
      />
    </svg>
  );
}

/**
 * Not found 404 — números grandes flutuando, com lupa e ground shadow.
 */
function NotFound404() {
  return (
    <svg
      viewBox="0 0 320 240"
      fill="none"
      className="w-full h-full"
      xmlns="http://www.w3.org/2000/svg"
    >
      <ellipse
        cx="160"
        cy="215"
        rx="120"
        ry="10"
        className="fill-brand-100/60 dark:fill-brand-500/10"
      />
      {/* 4 (left) */}
      <text
        x="50"
        y="170"
        className="fill-current"
        fontSize="120"
        fontWeight="800"
        fontFamily="system-ui, -apple-system, sans-serif"
      >
        4
      </text>
      {/* 0 (middle) — desenhado como círculo para destacar */}
      <circle
        cx="160"
        cy="130"
        r="48"
        className="fill-brand-50 dark:fill-brand-500/15 stroke-current"
        strokeWidth="8"
      />
      {/* Magnifying glass inside the 0 */}
      <circle
        cx="160"
        cy="130"
        r="22"
        className="fill-white dark:fill-gray-900 stroke-current opacity-70"
        strokeWidth="3"
      />
      <line
        x1="176"
        y1="146"
        x2="190"
        y2="160"
        className="stroke-current"
        strokeWidth="4"
        strokeLinecap="round"
      />
      {/* 4 (right) */}
      <text
        x="220"
        y="170"
        className="fill-current"
        fontSize="120"
        fontWeight="800"
        fontFamily="system-ui, -apple-system, sans-serif"
      >
        4
      </text>
      {/* Accents */}
      <circle
        cx="40"
        cy="60"
        r="5"
        className="fill-brand-300/70 dark:fill-brand-500/30"
      />
      <rect
        x="275"
        y="50"
        width="10"
        height="10"
        rx="2"
        className="fill-brand-200/70 dark:fill-brand-500/20"
        transform="rotate(25 280 55)"
      />
      <circle
        cx="290"
        cy="180"
        r="4"
        className="fill-brand-300/70 dark:fill-brand-500/30"
      />
    </svg>
  );
}

/**
 * Server error 500 — engrenagem com warning + linhas representando crash.
 */
function ServerError() {
  return (
    <svg
      viewBox="0 0 320 240"
      fill="none"
      className="w-full h-full"
      xmlns="http://www.w3.org/2000/svg"
    >
      <ellipse
        cx="160"
        cy="215"
        rx="110"
        ry="10"
        className="fill-warning-100/70 dark:fill-warning-500/10"
      />
      {/* Server box */}
      <rect
        x="95"
        y="80"
        width="130"
        height="110"
        rx="8"
        className="fill-white dark:fill-gray-800 stroke-current"
        strokeWidth="2"
      />
      {/* Server racks */}
      <rect
        x="105"
        y="95"
        width="110"
        height="20"
        rx="3"
        className="fill-brand-50 dark:fill-brand-500/10 stroke-current opacity-60"
        strokeWidth="1.5"
      />
      <circle cx="115" cy="105" r="3" className="fill-error-500" />
      <line
        x1="125"
        y1="105"
        x2="200"
        y2="105"
        className="stroke-current opacity-30"
        strokeWidth="2"
        strokeLinecap="round"
      />
      <rect
        x="105"
        y="125"
        width="110"
        height="20"
        rx="3"
        className="fill-brand-50 dark:fill-brand-500/10 stroke-current opacity-60"
        strokeWidth="1.5"
      />
      <circle cx="115" cy="135" r="3" className="fill-warning-500" />
      <line
        x1="125"
        y1="135"
        x2="180"
        y2="135"
        className="stroke-current opacity-30"
        strokeWidth="2"
        strokeLinecap="round"
      />
      <rect
        x="105"
        y="155"
        width="110"
        height="20"
        rx="3"
        className="fill-brand-50 dark:fill-brand-500/10 stroke-current opacity-60"
        strokeWidth="1.5"
      />
      <circle cx="115" cy="165" r="3" className="fill-error-500" />
      <line
        x1="125"
        y1="165"
        x2="195"
        y2="165"
        className="stroke-current opacity-30"
        strokeWidth="2"
        strokeLinecap="round"
      />
      {/* Warning triangle floating top right */}
      <path
        d="M240 50 L270 90 L210 90 Z"
        className="fill-warning-100 dark:fill-warning-500/20 stroke-warning-500"
        strokeWidth="3"
        strokeLinejoin="round"
      />
      <line
        x1="240"
        y1="65"
        x2="240"
        y2="78"
        className="stroke-warning-700 dark:stroke-warning-400"
        strokeWidth="3"
        strokeLinecap="round"
      />
      <circle cx="240" cy="84" r="2" className="fill-warning-700 dark:fill-warning-400" />
      {/* Crash lines */}
      <line
        x1="50"
        y1="85"
        x2="65"
        y2="85"
        className="stroke-current opacity-30"
        strokeWidth="2"
        strokeLinecap="round"
      />
      <line
        x1="45"
        y1="105"
        x2="65"
        y2="105"
        className="stroke-current opacity-30"
        strokeWidth="2"
        strokeLinecap="round"
      />
      <line
        x1="55"
        y1="125"
        x2="70"
        y2="125"
        className="stroke-current opacity-30"
        strokeWidth="2"
        strokeLinecap="round"
      />
    </svg>
  );
}

/**
 * Welcome / onboarding — pessoa estilizada (sem rosto) acenando + balão de
 * boas-vindas. Para empty states de onboarding (ex: primeiro acesso).
 */
function Welcome() {
  return (
    <svg
      viewBox="0 0 320 240"
      fill="none"
      className="w-full h-full"
      xmlns="http://www.w3.org/2000/svg"
    >
      <ellipse
        cx="160"
        cy="215"
        rx="100"
        ry="9"
        className="fill-brand-100/60 dark:fill-brand-500/10"
      />
      {/* Speech bubble */}
      <path
        d="M190 50 Q190 35 205 35 L280 35 Q295 35 295 50 L295 85 Q295 100 280 100 L235 100 L218 115 L222 100 L205 100 Q190 100 190 85 Z"
        className="fill-white dark:fill-gray-800 stroke-current"
        strokeWidth="2"
      />
      {/* Sparkle inside bubble */}
      <path
        d="M242 60 L248 70 L258 65 L248 75 L252 87 L240 78 L228 87 L232 75 L222 65 L232 70 Z"
        className="fill-current opacity-80"
      />
      {/* Person body */}
      <rect
        x="120"
        y="120"
        width="80"
        height="80"
        rx="40"
        className="fill-brand-100 dark:fill-brand-500/20 stroke-current"
        strokeWidth="2"
      />
      {/* Head */}
      <circle
        cx="160"
        cy="100"
        r="28"
        className="fill-brand-200 dark:fill-brand-500/30 stroke-current"
        strokeWidth="2"
      />
      {/* Waving arm */}
      <path
        d="M195 145 Q220 130 230 105 Q232 95 238 95"
        className="stroke-current"
        strokeWidth="6"
        strokeLinecap="round"
        fill="none"
      />
      {/* Hand */}
      <circle
        cx="240"
        cy="93"
        r="8"
        className="fill-brand-200 dark:fill-brand-500/30 stroke-current"
        strokeWidth="2"
      />
      {/* Floating dots */}
      <circle cx="55" cy="70" r="4" className="fill-brand-300/70 dark:fill-brand-500/30" />
      <circle cx="50" cy="170" r="5" className="fill-brand-200/70 dark:fill-brand-500/20" />
      <rect
        x="270"
        y="170"
        width="10"
        height="10"
        rx="2"
        className="fill-brand-300/70 dark:fill-brand-500/30"
        transform="rotate(15 275 175)"
      />
    </svg>
  );
}

const ILLUSTRATIONS: Record<IllustrationName, React.FC> = {
  "empty-catalog": EmptyCatalog,
  "integrations-empty": IntegrationsEmpty,
  "no-results": NoResults,
  "payment-success": PaymentSuccess,
  "not-found-404": NotFound404,
  "server-error": ServerError,
  welcome: Welcome,
};
