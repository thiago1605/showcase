"use client";

import React from "react";

/**
 * Header purple-gradient padrão da plataforma — aplica a mesma linguagem visual
 * do dropdown do usuário (pill purple) em escala de página.
 *
 * Identidade da página vai no purple: título + descrição + badges/contexto.
 * Ações primárias (CTA principal, period picker, filtros) ficam no canto
 * direito, invertidas com bg branco/glass pra contrastar com o purple.
 *
 * Cobre 3 padrões comuns:
 *   - Página de listagem: title + subtitle + CTA "+ Novo X"
 *   - Página detail: title + subtitle + badges contextuais + actions
 *   - Página com filtro: title + subtitle + period picker / search à direita
 *
 * Estrutura igual ao header do dropdown:
 *   - bg gradient: Royal Violet (brand-500) → Deep Violet (brand-700),
 *     range amplo de lightness (69% → 50%) que captura o efeito highlight →
 *     shadow visto na logo do grupo Fellow. Substituiu 500→600 (range estreito
 *     que ficava lavando pálido) e 500→via-600→to-300 (terminava em lavanda).
 *   - Shine sutil (white/15 → transparent) na metade superior
 *   - shadow-md tinted brand-500/20 (Royal Violet glow, sutil)
 *   - rounded-3xl pra harmonizar com os outros cards do dashboard
 *     (KPI cards, charts, rankings — todos `rounded-3xl` desde maio/2026).
 */
export function PageHeader({
  title,
  subtitle,
  badge,
  avatar,
  decorIcon,
  actions,
  children,
  size = "default",
}: {
  /** Título primário da página, em white. */
  title: string;
  /** Linha secundária em white/80, abaixo do título. Opcional. */
  subtitle?: string;
  /**
   * Badge contextual antes do título (status, categoria, etc).
   * Renderiza acima do título em pill style com bg white/15.
   */
  badge?: React.ReactNode;
  /**
   * Avatar/ícone INLINE à esquerda do título — usado em páginas que têm
   * identidade visual de um recurso específico (/profile). Caller controla
   * shape/size; recomendado h-14 w-14 ou h-16 w-16.
   */
  avatar?: React.ReactNode;
  /**
   * Ícone decorativo grande como MARCA D'ÁGUA — renderiza absoluto no canto
   * esquerdo do hero, atrás do texto, em white/10. Caller passa apenas o
   * <svg viewBox="0 0 24 24">...</svg> com paths — PageHeader controla
   * tamanho (w-44 h-44), opacidade e posição. Só aparece com size="hero".
   */
  decorIcon?: React.ReactNode;
  /** Slot direito — botões/pickers/filtros. */
  actions?: React.ReactNode;
  /**
   * Slot abaixo do título — usado pra rich content como métricas inline,
   * sub-tabs, etc. Opcional.
   */
  children?: React.ReactNode;
  /**
   * "default" para a maioria das pages.
   * "hero" para pages de entrada (Painel, Insights) que pedem mais presença
   * — padding maior, title maior, glow decorativo no canto, suporte a
   * decorIcon como watermark.
   */
  size?: "default" | "hero";
}) {
  const isHero = size === "hero";
  return (
    <div
      className={`relative overflow-hidden rounded-3xl bg-gradient-to-br from-brand-500 to-brand-700 shadow-md shadow-brand-500/20 ${
        isHero ? "px-6 sm:px-8 py-10 sm:py-14" : "px-6 py-7 sm:py-9"
      }`}
    >
      {/* Shine sutil no topo — mesma técnica do pill do user dropdown.
          Simula luz batendo na curvatura, dá sensação premium. */}
      <span
        aria-hidden="true"
        className="pointer-events-none absolute inset-x-0 top-0 h-1/2 bg-gradient-to-b from-white/15 to-transparent"
      />
      {/* Glow decorativo só em hero — orbe purple soft no canto inferior
          direito, blur pesado, simula reflexo de luz. */}
      {isHero && (
        <span
          aria-hidden="true"
          className="pointer-events-none absolute -bottom-20 -right-20 w-72 h-72 rounded-full bg-white/15 blur-3xl"
        />
      )}
      {/* Marca d'água decorativa — ícone em white/10 INSET na esquerda.
          Posicionado em left-6 (24px do edge) com tamanho calibrado pra caber
          inteiro dentro da altura do hero (~140px) sem cortar top/bottom.
          Em "hero" usa w-32 (era w-44 e cortava); "default" usa w-20.
          Fica atrás do texto via z-0. */}
      {decorIcon && (
        <span
          aria-hidden="true"
          className={`pointer-events-none absolute top-1/2 -translate-y-1/2 text-white/10 ${
            isHero
              ? "left-6 [&>svg]:w-32 [&>svg]:h-32 sm:[&>svg]:w-36 sm:[&>svg]:h-36"
              : "left-3 [&>svg]:w-20 [&>svg]:h-20 sm:[&>svg]:w-24 sm:[&>svg]:h-24"
          }`}
        >
          {decorIcon}
        </span>
      )}

      <div className="relative flex items-start justify-between gap-4 flex-wrap">
        <div className={`flex items-start gap-4 min-w-0 flex-1 ${
          decorIcon
            ? isHero
              ? "sm:pl-32 md:pl-40"
              : "sm:pl-20 md:pl-24"
            : ""
        }`}>
          {avatar && <div className="shrink-0">{avatar}</div>}
          <div className="min-w-0 flex-1">
            {badge && <div className="mb-2">{badge}</div>}
            <h1
              className={`font-bold text-white tracking-tight leading-tight ${
                isHero ? "text-2xl sm:text-3xl lg:text-4xl" : "text-xl sm:text-2xl"
              }`}
            >
              {title}
            </h1>
            {subtitle && (
              <p
                className={`text-white/80 leading-snug ${
                  isHero ? "mt-2 text-sm sm:text-base" : "mt-1 text-sm"
                }`}
              >
                {subtitle}
              </p>
            )}
          </div>
        </div>
        {actions && (
          <div className="shrink-0 flex items-center gap-2">{actions}</div>
        )}
      </div>

      {children && <div className="relative mt-4">{children}</div>}
    </div>
  );
}

/**
 * Chip de metadata para usar dentro do PageHeader (status, CNPJ, etc).
 * Cor white-on-purple — substitui o MetaChip do SellerProfileHeader que
 * era para bg branco. Glass-like via backdrop-blur + bg-white/15.
 */
export function PageHeaderChip({
  label,
  value,
  tone = "default",
}: {
  label?: string;
  value: React.ReactNode;
  /** "default" usa white/15. "success" usa green-300/25 pra status ativo. */
  tone?: "default" | "success" | "warning" | "error";
}) {
  const toneCls = {
    default: "bg-white/15 ring-white/20",
    success: "bg-success-400/25 ring-success-200/30",
    warning: "bg-warning-400/25 ring-warning-200/30",
    error: "bg-error-400/25 ring-error-200/30",
  }[tone];
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 ring-1 ring-inset backdrop-blur-sm whitespace-nowrap ${toneCls}`}
    >
      {label && (
        <span className="text-[10px] font-semibold uppercase tracking-wider text-white/70">
          {label}
        </span>
      )}
      <span className="text-xs font-medium text-white tabular-nums">
        {value}
      </span>
    </span>
  );
}

/**
 * Variantes pré-prontas dos elementos comuns que vivem dentro do PageHeader.
 * Padroniza o look pra que botões/badges adicionados via `actions` ou `badge`
 * combinem com o bg purple.
 */

/** Badge inline pra contexto no PageHeader (uppercase, bg-white/15). */
export function PageHeaderBadge({ children }: { children: React.ReactNode }) {
  return (
    <span className="inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-semibold bg-white/15 text-white backdrop-blur-sm">
      {children}
    </span>
  );
}

/**
 * Botão CTA padrão pra ações dentro do PageHeader.
 * Bg branco com text purple — invertido pra contrastar com o bg purple do header.
 */
export function PageHeaderButton({
  href,
  onClick,
  children,
  variant = "primary",
}: {
  href?: string;
  onClick?: () => void;
  children: React.ReactNode;
  /** primary = bg branco + texto purple; ghost = transparente + texto branco. */
  variant?: "primary" | "ghost";
}) {
  const cls =
    variant === "primary"
      ? "h-10 inline-flex items-center rounded-lg bg-white hover:bg-white/95 px-4 text-sm font-semibold text-brand-700 shadow-sm transition-all whitespace-nowrap"
      : "h-10 inline-flex items-center rounded-lg bg-white/10 hover:bg-white/20 px-4 text-sm font-semibold text-white border border-white/20 transition-colors whitespace-nowrap";

  if (href) {
    // Usar <a> simples pra evitar import circular de Link em todos os usuários
    return (
      <a href={href} className={cls}>
        {children}
      </a>
    );
  }
  return (
    <button type="button" onClick={onClick} className={cls}>
      {children}
    </button>
  );
}
