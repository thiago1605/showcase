"use client";

import Link from "next/link";
import type { SellerTierCode } from "@/types";

/**
 * Badge visual pro tier do seller. Cor + tipografia seguem hierarquia premium:
 *   SILVER:   cinza neutro (entrada — sem aspirational visual)
 *   GOLD:     dourado fosco (primeira conquista)
 *   DIAMOND:  branco iridescente cyan (transição pra escala)
 *   BLACK:    preto + borda dourada (status)
 *   INFINITE: gradient roxo Fellow + tipografia destaque (convite exclusivo)
 *
 * Quando <c>asLink</c> está true, vira <Link> pra /tier. Default true.
 * Quando <c>size="sm"</c>, render compacto pro header. "md" é o card do dashboard.
 */
interface TierBadgeProps {
  tier: SellerTierCode;
  size?: "sm" | "md";
  asLink?: boolean;
  className?: string;
}

// Tier names mantidos em inglês (brand proper nouns — pattern fintech tipo
// Amex Centurion / Visa Infinite). Descrições/funcionais em PT-BR.
//
// Escalada visual:
//   SILVER:  prata neutra (entrada)
//   GOLD:    dourado quente (primeira conquista)
//   DIAMOND: branco iridescente cyan — substitui visualmente "Platinum" que
//            era a mesma cor de Silver (prata e platina são ambos prateados
//            na vida real). Backend renomeou PLATINUM→DIAMOND na Sprint 2.
//   BLACK:   preto + accent dourado (status)
//   INFINITE: gradient roxo Fellow brand (exclusividade)
const TIER_STYLES: Record<
  SellerTierCode,
  { label: string; cls: string; ringCls: string }
> = {
  SILVER: {
    label: "Silver",
    cls: "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300",
    ringCls: "ring-1 ring-inset ring-gray-300/60 dark:ring-gray-600/60",
  },
  GOLD: {
    label: "Gold",
    cls: "bg-amber-50 text-amber-800 dark:bg-amber-900/30 dark:text-amber-200",
    ringCls: "ring-1 ring-inset ring-amber-400/60 dark:ring-amber-500/40",
  },
  DIAMOND: {
    label: "Diamond",
    cls: "bg-gradient-to-br from-white via-cyan-50 to-sky-100 text-sky-900 dark:from-cyan-950/30 dark:via-sky-950/40 dark:to-sky-900/40 dark:text-cyan-100",
    ringCls: "ring-1 ring-inset ring-cyan-400/70 dark:ring-cyan-400/50",
  },
  BLACK: {
    label: "Black",
    cls: "bg-gray-900 text-amber-100 dark:bg-black dark:text-amber-200",
    ringCls: "ring-1 ring-inset ring-amber-500/40",
  },
  INFINITE: {
    label: "Infinite",
    cls: "bg-gradient-to-r from-brand-500 to-brand-700 text-white",
    ringCls: "ring-1 ring-inset ring-brand-300/60",
  },
};

export function TierBadge({
  tier,
  size = "sm",
  asLink = true,
  className = "",
}: TierBadgeProps) {
  const style = TIER_STYLES[tier];
  const sizeCls =
    size === "md"
      ? "px-3 py-1 text-sm tracking-wide font-semibold"
      : "px-2 py-0.5 text-[11px] tracking-wider font-bold";

  const content = (
    <span
      className={`inline-flex items-center rounded-md uppercase whitespace-nowrap ${sizeCls} ${style.cls} ${style.ringCls} ${className}`}
      title={`Nível ${style.label}`}
    >
      {style.label}
    </span>
  );

  if (asLink) {
    return (
      <Link href="/tier" className="hover:opacity-80 transition-opacity">
        {content}
      </Link>
    );
  }

  return content;
}

/**
 * Badge separado pro Pioneiro — orthogonal a nível. Aparece sempre ao
 * lado do TierBadge quando aplicável. Visual estrela + número formatado.
 *
 * Naming: "Pioneiro" (português) em vez de "Founding" (anglicismo). Mantém
 * o nome técnico em código (FoundingBadge, isFoundingSeller, foundingNumber)
 * pra alinhar com o backend e API contract — só o user-facing fica em PT-BR.
 */
interface FoundingBadgeProps {
  number: number;
  size?: "sm" | "md";
  asLink?: boolean;
  className?: string;
}

export function FoundingBadge({
  number,
  size = "sm",
  asLink = true,
  className = "",
}: FoundingBadgeProps) {
  const sizeCls =
    size === "md"
      ? "px-3 py-1 text-sm font-semibold"
      : "px-2 py-0.5 text-[11px] font-bold";

  const formatted = String(number).padStart(3, "0");

  const content = (
    <span
      className={`inline-flex items-center gap-1 rounded-md bg-brand-50 text-brand-700 ring-1 ring-inset ring-brand-300/60 whitespace-nowrap dark:bg-brand-500/15 dark:text-brand-300 dark:ring-brand-400/40 ${sizeCls} ${className}`}
      title={`Pioneiro #${formatted}`}
    >
      <svg
        viewBox="0 0 24 24"
        fill="currentColor"
        className={size === "md" ? "h-3.5 w-3.5" : "h-3 w-3"}
        aria-hidden="true"
      >
        <path d="M12 .587l3.668 7.43 8.2 1.193-5.934 5.783 1.4 8.166L12 18.897l-7.334 3.862 1.4-8.166L.132 9.21l8.2-1.193z" />
      </svg>
      Pioneiro #{formatted}
    </span>
  );

  if (asLink) {
    return (
      <Link href="/tier" className="hover:opacity-80 transition-opacity">
        {content}
      </Link>
    );
  }

  return content;
}
