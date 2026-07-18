"use client";

import Link from "next/link";
import { useSellerTier } from "@/hooks/useSellerTier";
import { useSellerProfile } from "@/hooks/useSellerProfile";
import { TierPremiumCard } from "@/components/tier/TierPremiumCard";
import type { SellerTierCode } from "@/types";

const TIER_PIX_RATE: Record<SellerTierCode, string> = {
  SILVER: "2,9% + R$ 0,47",
  GOLD: "2,7% + R$ 0,39",
  DIAMOND: "2,5% + R$ 0,29",
  BLACK: "2,4% + R$ 0,19",
  INFINITE: "personalizada",
};

const TIER_NEXT_LABEL: Record<SellerTierCode, string> = {
  SILVER: "Gold",
  GOLD: "Diamond",
  DIAMOND: "Black",
  BLACK: "Infinite",
  INFINITE: "—",
};

// Label do tier atual pros extremos da barra de progresso (esquerda).
// Separado do `currentTier` raw porque vai ser usado em texto user-facing
// (com case correto) — o enum é UPPERCASE técnico.
const TIER_LABEL_SHORT: Record<SellerTierCode, string> = {
  SILVER: "Silver",
  GOLD: "Gold",
  DIAMOND: "Diamond",
  BLACK: "Black",
  INFINITE: "Infinite",
};

function formatBRL(value: number): string {
  return value.toLocaleString("pt-BR", {
    style: "currency",
    currency: "BRL",
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
}

/**
 * Widget compacto de tier no dashboard. Mini card (14rem ≈ 224px) à esquerda
 * + painel horizontal de stats à direita (taxa PIX + barra de progresso +
 * gap pro próximo tier). Linka pra /tier ao clicar.
 *
 * Decisão de design: o hero full-size (igual ao da /tier) ocupava ~370px no
 * topo do dashboard, empurrando os números operacionais (receita, transações)
 * pra baixo da fold. A versão compacta mantém a identidade visual do cartão
 * mas libera espaço pro dashboard cumprir sua função operacional.
 *
 * Estados:
 *  - loading: skeleton mantendo footprint do widget
 *  - error: card vazio (operadores da plataforma têm 403 no /tier)
 *  - BLACK/INFINITE: callout discreto em vez de barra de progresso
 *  - demais: barra horizontal + texto "Faltam R$ X pra Y"
 */
export function TierCard() {
  const { tier, loading, error } = useSellerTier();
  const { profile } = useSellerProfile();
  const sellerName =
    profile?.tradeName?.trim() || profile?.legalName || "Seller";

  if (loading) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white p-5 dark:border-gray-800 dark:bg-white/[0.03]">
        <div className="flex items-center gap-5">
          {/* Skeleton card mantém aspect-ratio do mini card (14rem × 1.586) */}
          <div className="aspect-[1.586/1] w-full max-w-[14rem] shrink-0 animate-pulse bg-gray-200 dark:bg-gray-800 rounded-2xl" />
          <div className="flex-1 min-w-0 space-y-3">
            <div className="h-3 w-24 animate-pulse bg-gray-200 dark:bg-gray-800 rounded" />
            <div className="h-7 w-40 animate-pulse bg-gray-200 dark:bg-gray-800 rounded" />
            <div className="h-2 w-full animate-pulse bg-gray-200 dark:bg-gray-800 rounded-full" />
            <div className="h-3 w-56 animate-pulse bg-gray-200 dark:bg-gray-800 rounded" />
          </div>
        </div>
      </div>
    );
  }

  if (error || !tier) return null;

  const isTopTier = tier.nextTier == null;
  const totalForNext = tier.tpv30dBrl + (tier.gapToNextBrl ?? 0);
  const progress = isTopTier
    ? 100
    : tier.gapToNextBrl != null && totalForNext > 0
      ? Math.max(
          0,
          Math.min(100, Math.round((tier.tpv30dBrl / totalForNext) * 100)),
        )
      : 0;

  return (
    <Link
      href="/tier"
      className="block group rounded-3xl border border-gray-200/60 bg-white p-5 transition-shadow hover:shadow-md dark:border-gray-800 dark:bg-white/[0.03]"
    >
      <div className="flex items-center gap-5">
        {/* MINI CARD à esquerda — sm (14rem ≈ 224px wide), estático sem hover
            effects. `w-56` (=14rem) fixo no wrapper porque dentro de flex
            container o `w-full` do card resolve pelo conteúdo interno (que
            ficou menor sem o seller name), encolhendo o card. */}
        <div className="shrink-0 w-56">
          <TierPremiumCard
            tier={tier.currentTier}
            sellerName={sellerName}
            foundingNumber={
              tier.isFoundingSeller ? tier.foundingNumber : null
            }
            size="sm"
          />
        </div>

        {/* STATS PANEL à direita — taxa + progresso. Identidade (nível, seller,
            Pioneiro) já está no card, então o painel é puro ACTION/STATUS. */}
        <div className="flex-1 min-w-0">
          {/* Label + chevron inline — antes o chevron ficava órfão no canto
              direito do widget inteiro, longe demais do label e do rate. */}
          <span className="inline-flex items-center gap-1 text-xs font-medium uppercase tracking-wider text-gray-500 dark:text-gray-400">
            Sua taxa PIX
            <svg
              className="h-3.5 w-3.5 transition-transform group-hover:translate-x-0.5"
              viewBox="0 0 20 20"
              fill="currentColor"
              aria-hidden="true"
            >
              <path
                fillRule="evenodd"
                d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
                clipRule="evenodd"
              />
            </svg>
          </span>
          <p className="text-2xl font-semibold text-gray-900 dark:text-white tabular-nums mt-1 mb-3">
            {TIER_PIX_RATE[tier.currentTier]}
          </p>

          {/* Barra de progresso compacta — só pros tiers com próximo automático.
              Labels dos extremos = tier atual à esquerda, próximo à direita.
              Antes o lado direito mostrava o valor R$ total (R$ 50k pra Gold)
              que era ambíguo: "R$ 50k é meta total ou quanto falta?". Labels
              de tier eliminam essa fricção. */}
          {!isTopTier && tier.gapToNextBrl != null && tier.nextTier && (
            <>
              <div className="mb-1.5 flex items-baseline justify-between gap-2 text-xs text-gray-500 dark:text-gray-400">
                <span className="font-medium text-gray-700 dark:text-gray-300">
                  {TIER_LABEL_SHORT[tier.currentTier]}
                </span>
                <span className="font-medium text-gray-700 dark:text-gray-300">
                  {TIER_NEXT_LABEL[tier.currentTier]}
                </span>
              </div>
              <div className="h-1.5 w-full overflow-hidden rounded-full bg-gray-100 dark:bg-gray-800">
                <div
                  className="h-full bg-brand-500 transition-all"
                  style={{ width: `${progress}%` }}
                  role="progressbar"
                  aria-valuenow={progress}
                  aria-valuemin={0}
                  aria-valuemax={100}
                />
              </div>
              <p className="mt-2 text-xs text-gray-600 dark:text-gray-400">
                <span className="font-medium text-gray-700 dark:text-gray-300 tabular-nums">
                  {progress}%
                </span>{" "}
                — faltam{" "}
                <span className="font-semibold text-gray-900 dark:text-white tabular-nums">
                  {formatBRL(tier.gapToNextBrl)}
                </span>{" "}
                pra {TIER_NEXT_LABEL[tier.currentTier]}
                {tier.nextTier && (
                  <>
                    {" "}
                    <span className="opacity-70">
                      ({TIER_PIX_RATE[tier.nextTier]})
                    </span>
                  </>
                )}
              </p>
            </>
          )}

          {/* Callouts pros tiers de topo — mais discretos pra não roubar cena */}
          {isTopTier && tier.currentTier === "BLACK" && (
            <p className="text-xs text-gray-600 dark:text-gray-400">
              Topo automático. O próximo é{" "}
              <span className="font-semibold">Infinite</span> — convite
              exclusivo Fellow.
            </p>
          )}

          {isTopTier && tier.currentTier === "INFINITE" && (
            <p className="text-xs text-gray-600 dark:text-gray-400">
              Você faz parte do{" "}
              <span className="font-semibold">Fellow Infinite Club</span>. Taxa
              negociada, suporte dedicado.
            </p>
          )}
        </div>
      </div>
    </Link>
  );
}
