"use client";

import { useSellerTier } from "@/hooks/useSellerTier";
import { useSellerProfile } from "@/hooks/useSellerProfile";
import { TierBadge, FoundingBadge } from "@/components/tier/TierBadge";
import { TierPremiumCard } from "@/components/tier/TierPremiumCard";
import { PageHeader } from "@/components/ui/PageHeader";
import type { SellerTierCode } from "@/types";

interface TierRow {
  code: SellerTierCode;
  label: string;
  volumeRange: string;
  pixRate: string;
  creditCash: string;
  creditInstallment: string;
}

/**
 * Fonte única dos números exibidos. Deve manter sincronia com
 * src/FellowCore.Application/Modules/Pricing/Options/TierPricingOptions.cs
 * (DefaultRates). Quando admin override a config via appsettings, este front
 * vai mostrar valores desatualizados — Sprint 2 considera buscar da API.
 */
const TIER_ROWS: TierRow[] = [
  {
    code: "SILVER",
    label: "Silver",
    volumeRange: "Até R$ 50 mil",
    pixRate: "2,9% + R$ 0,47",
    creditCash: "4,99% + R$ 0,49",
    creditInstallment: "5,99% + R$ 0,49",
  },
  {
    code: "GOLD",
    label: "Gold",
    volumeRange: "R$ 50 mil a R$ 250 mil",
    pixRate: "2,7% + R$ 0,39",
    creditCash: "4,89% + R$ 0,49",
    creditInstallment: "5,89% + R$ 0,49",
  },
  {
    // "Diamond" elimina ambiguidade visual com Silver (Ag/Pt são prateados na
    // vida real). Backend renomeou PLATINUM→DIAMOND na Sprint 2 — label e code
    // agora batem.
    code: "DIAMOND",
    label: "Diamond",
    volumeRange: "R$ 250 mil a R$ 1 milhão",
    pixRate: "2,5% + R$ 0,29",
    creditCash: "4,79% + R$ 0,49",
    creditInstallment: "5,79% + R$ 0,49",
  },
  {
    code: "BLACK",
    label: "Black",
    volumeRange: "R$ 1 milhão a R$ 10 milhões",
    pixRate: "2,4% + R$ 0,19",
    creditCash: "4,69% + R$ 0,49",
    creditInstallment: "5,69% + R$ 0,49",
  },
  {
    code: "INFINITE",
    label: "Infinite",
    volumeRange: "Convite exclusivo",
    pixRate: "personalizada",
    creditCash: "personalizada",
    creditInstallment: "personalizada",
  },
];

const TIER_PERKS: Record<SellerTierCode, string[]> = {
  SILVER: [
    "Taxa padrão Fellow Pay",
    "Suporte por chat (horário comercial)",
    "Todas as funcionalidades do produto",
  ],
  GOLD: [
    "Redução de taxa em todos os métodos",
    "Suporte priorizado",
    "Webhooks com 99,9% SLA",
    "Acesso antecipado a novas features",
  ],
  DIAMOND: [
    "Redução adicional de taxa",
    "Suporte dedicado",
    "Onboarding white-glove de novos produtos",
    "Convite para eventos privados",
    "Reports financeiros customizados",
  ],
  BLACK: [
    "Taxa otimizada (próxima do custo)",
    "Account manager dedicado",
    "Placas personalizadas + reconhecimento no Leaderboard",
    "Viagens e experiências premium",
    "Networking estratégico Fellow Infinite Club",
  ],
  INFINITE: [
    "Taxa negociada individualmente",
    "Acesso ao Fellow Infinite Club",
    "Co-criação de roadmap com a Fellow Pay",
    "Eventos privados Infinite",
    "Tudo do Black, sem limites",
  ],
};

function formatBRL(value: number): string {
  return value.toLocaleString("pt-BR", {
    style: "currency",
    currency: "BRL",
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
}

/** Cadeado outline — substitui o emoji 🔒 que soava "gamificado demais". */
function LockIcon() {
  return (
    <svg
      className="h-3.5 w-3.5"
      viewBox="0 0 20 20"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      aria-hidden="true"
    >
      <rect x="4" y="9" width="12" height="8" rx="1.5" />
      <path d="M7 9V6.5a3 3 0 016 0V9" strokeLinecap="round" />
    </svg>
  );
}

/** Estrela outline — substitui 👑 (que era apelativo de MLM). */
function StarOutlineIcon() {
  return (
    <svg
      className="h-3.5 w-3.5"
      viewBox="0 0 20 20"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d="M10 2.5l2.45 4.96 5.476.797-3.963 3.864.936 5.459L10 15l-4.9 2.58.936-5.459-3.963-3.864 5.477-.797z" />
    </svg>
  );
}

function CheckIcon({ className = "" }: { className?: string }) {
  return (
    <svg
      className={`h-4 w-4 ${className}`}
      viewBox="0 0 20 20"
      fill="currentColor"
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        d="M16.704 4.153a.75.75 0 01.143 1.052l-8 10.5a.75.75 0 01-1.127.075l-4.5-4.5a.75.75 0 011.06-1.06l3.894 3.893 7.48-9.817a.75.75 0 011.05-.143z"
        clipRule="evenodd"
      />
    </svg>
  );
}

function ChevronRightIcon({ className = "" }: { className?: string }) {
  return (
    <svg
      className={`h-4 w-4 ${className}`}
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
  );
}

export function TierPageClient() {
  const { tier, loading, error } = useSellerTier();
  const { profile } = useSellerProfile();
  const sellerName =
    profile?.tradeName?.trim() || profile?.legalName || "Seller";

  if (loading) {
    return (
      <div className="space-y-6">
        <PageHeader
          size="hero"
          title="Meu nível"
          subtitle="Quanto mais você processa, menos paga. Sem mensalidade, sem fidelidade."
          decorIcon={
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <path d="M12 2l3.09 6.26 6.91 1.01-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14l-5-4.87 6.91-1.01z" />
            </svg>
          }
        />
        <div className="h-80 w-full animate-pulse bg-gray-100 dark:bg-gray-800 rounded-2xl" />
      </div>
    );
  }

  if (error || !tier) {
    return (
      <div className="space-y-6">
        <PageHeader
          size="hero"
          title="Meu nível"
          subtitle="Quanto mais você processa, menos paga. Sem mensalidade, sem fidelidade."
          decorIcon={
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <path d="M12 2l3.09 6.26 6.91 1.01-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14l-5-4.87 6.91-1.01z" />
            </svg>
          }
        />
        <div className="rounded-2xl border border-red-200 bg-red-50 p-6 dark:border-red-900 dark:bg-red-900/20">
          <p className="text-sm text-red-700 dark:text-red-300">
            {error ?? "Não foi possível carregar seu nível."}
          </p>
        </div>
      </div>
    );
  }

  const currentRow = TIER_ROWS.find((r) => r.code === tier.currentTier)!;
  const nextRow = tier.nextTier
    ? TIER_ROWS.find((r) => r.code === tier.nextTier)
    : null;

  const totalForNext = tier.tpv30dBrl + (tier.gapToNextBrl ?? 0);
  const progress =
    tier.nextTier == null
      ? 100
      : totalForNext > 0
        ? Math.max(
            0,
            Math.min(100, Math.round((tier.tpv30dBrl / totalForNext) * 100)),
          )
        : 0;

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Meu nível"
        subtitle="Quanto mais você processa, menos paga. Sem mensalidade, sem fidelidade."
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="M12 2l3.09 6.26 6.91 1.01-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14l-5-4.87 6.91-1.01z" />
          </svg>
        }
      />

      {/* HERO — cartão premium (estilo Amex) + stats laterais (incl. progresso).
          Grid com coluna 1 explícita em 28rem (448px = max-w-md do cartão).
          `auto` encolhia ao mínimo do conteúdo (~280px), deformava o cartão.
          `28rem_1fr` mantém o cartão no tamanho cheio + stats fills rest. */}
      <section className="rounded-2xl border border-gray-200 bg-white p-6 dark:border-gray-800 dark:bg-white/[0.03]">
        <div className="grid grid-cols-1 md:grid-cols-[25rem_1fr] gap-8 items-center">
          {/* CARD PREMIUM */}
          <div className="flex justify-center md:justify-start">
            <TierPremiumCard
              tier={tier.currentTier}
              sellerName={sellerName}
              foundingNumber={
                tier.isFoundingSeller ? tier.foundingNumber : null
              }
            />
          </div>

          {/* STATS PANEL — taxa + volume + barra de progresso + callout */}
          <div className="space-y-5">
            <div className="grid grid-cols-2 gap-4">
              <div>
                <p className="text-xs font-medium uppercase tracking-wider text-gray-500 dark:text-gray-400">
                  Sua taxa PIX
                </p>
                <p className="mt-1 text-xl font-semibold text-gray-900 dark:text-white">
                  {currentRow.pixRate}
                </p>
              </div>
              <div>
                <p className="text-xs font-medium uppercase tracking-wider text-gray-500 dark:text-gray-400">
                  Volume (90 dias)
                </p>
                <p className="mt-1 text-xl font-semibold text-gray-900 dark:text-white">
                  {formatBRL(tier.tpv30dBrl)}
                </p>
              </div>
            </div>

            {/* Barra de progresso integrada no stats panel */}
            {nextRow && tier.gapToNextBrl != null && (
              <div>
                <div className="mb-2 flex items-baseline justify-between gap-2 text-xs text-gray-500 dark:text-gray-400">
                  <span className="font-medium text-gray-700 dark:text-gray-300">
                    {progress}% até {nextRow.label}
                  </span>
                  <span>{formatBRL(totalForNext)}</span>
                </div>
                <div className="h-2 w-full overflow-hidden rounded-full bg-gray-100 dark:bg-gray-800">
                  <div
                    className="h-full bg-brand-500 transition-all"
                    style={{ width: `${progress}%` }}
                    role="progressbar"
                    aria-valuenow={progress}
                    aria-valuemin={0}
                    aria-valuemax={100}
                  />
                </div>
                <p className="mt-3 text-sm text-gray-700 dark:text-gray-300">
                  Faltam{" "}
                  <span className="font-semibold text-gray-900 dark:text-white">
                    {formatBRL(tier.gapToNextBrl)}
                  </span>{" "}
                  para desbloquear{" "}
                  <span className="font-semibold">{nextRow.label}</span> —{" "}
                  <span className="font-mono">{nextRow.pixRate}</span> no PIX.
                </p>
              </div>
            )}

            {!nextRow && tier.currentTier === "BLACK" && (
              <div className="rounded-xl bg-gradient-to-r from-brand-50 to-brand-100 p-3 dark:from-brand-500/10 dark:to-brand-700/10">
                <p className="text-sm text-gray-700 dark:text-gray-300">
                  Você está no topo automático. O próximo nível é{" "}
                  <span className="font-semibold">Infinite</span> — convite
                  exclusivo da Fellow Pay.
                </p>
              </div>
            )}

            {!nextRow && tier.currentTier === "INFINITE" && (
              <div className="rounded-xl bg-gradient-to-r from-brand-50 to-brand-100 p-3 dark:from-brand-500/10 dark:to-brand-700/10">
                <p className="text-sm text-gray-700 dark:text-gray-300">
                  Você faz parte do{" "}
                  <span className="font-semibold">Fellow Infinite Club</span>.
                  Taxas negociadas, suporte dedicado, acesso completo.
                </p>
              </div>
            )}
          </div>
        </div>
      </section>

      {/* TABELA COMPARATIVA */}
      <section className="rounded-2xl border border-gray-200 bg-white overflow-hidden dark:border-gray-800 dark:bg-white/[0.03]">
        <div className="px-6 py-4 border-b border-gray-200 dark:border-gray-800">
          <h2 className="text-base font-semibold text-gray-900 dark:text-white">
            Tabela de níveis
          </h2>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
            Quanto mais você processa, menos paga. Sem mensalidade, sem
            fidelidade.
          </p>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 dark:bg-white/[0.02]">
              <tr className="text-left text-xs uppercase tracking-wider text-gray-500 dark:text-gray-400">
                <th className="px-6 py-3 font-medium">Nível</th>
                <th className="px-6 py-3 font-medium">
                  Volume processado (90 dias)
                </th>
                <th className="px-6 py-3 font-medium">PIX</th>
                <th className="px-6 py-3 font-medium">Crédito à vista</th>
                <th className="px-6 py-3 font-medium">Crédito parcelado</th>
                <th className="px-6 py-3 font-medium text-right">Status</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-200 dark:divide-gray-800">
              {TIER_ROWS.map((row) => {
                const isCurrent = row.code === tier.currentTier;
                const isNext = row.code === tier.nextTier;
                const isInfinite = row.code === "INFINITE";

                // Linhas uniformes — antes coloríamos current (brand) e next (amber),
                // mas isso criava inconsistência visual com os 3 não-destacados.
                // O status column ("Seu nível" / "Próximo") carrega a informação.
                return (
                  <tr key={row.code}>
                    <td className="px-6 py-4">
                      <TierBadge tier={row.code} size="sm" asLink={false} />
                    </td>
                    <td className="px-6 py-4 text-gray-700 dark:text-gray-300">
                      {row.volumeRange}
                    </td>
                    {isInfinite ? (
                      <td
                        colSpan={3}
                        className="px-6 py-4 italic text-gray-500 dark:text-gray-400"
                      >
                        Taxa personalizada — negociada caso a caso
                      </td>
                    ) : (
                      <>
                        <td className="px-6 py-4 font-mono text-gray-900 dark:text-white">
                          {row.pixRate}
                        </td>
                        <td className="px-6 py-4 font-mono text-gray-700 dark:text-gray-300">
                          {row.creditCash}
                        </td>
                        <td className="px-6 py-4 font-mono text-gray-700 dark:text-gray-300">
                          {row.creditInstallment}
                        </td>
                      </>
                    )}
                    <td className="px-6 py-4 text-right whitespace-nowrap">
                      {isCurrent && (
                        <span className="inline-flex items-center gap-1 text-xs font-medium text-brand-600 dark:text-brand-300">
                          <CheckIcon className="text-brand-500" />
                          Seu nível
                        </span>
                      )}
                      {isNext && !isCurrent && (
                        <span className="inline-flex items-center gap-1 text-xs font-medium text-amber-700 dark:text-amber-300">
                          <LockIcon />
                          Próximo
                        </span>
                      )}
                      {!isCurrent && !isNext && row.code === "INFINITE" && (
                        <span className="inline-flex items-center gap-1 text-xs font-medium text-gray-500 dark:text-gray-400">
                          <StarOutlineIcon />
                          Por convite
                        </span>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>

      {/* BENEFÍCIOS */}
      <section className="grid grid-cols-1 md:grid-cols-2 gap-6">
        <div className="rounded-2xl border border-gray-200 bg-white p-6 dark:border-gray-800 dark:bg-white/[0.03]">
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white mb-4">
            O que você tem hoje ({currentRow.label})
          </h3>
          <ul className="space-y-2">
            {TIER_PERKS[tier.currentTier].map((perk) => (
              <li
                key={perk}
                className="flex items-start gap-2 text-sm text-gray-700 dark:text-gray-300"
              >
                <CheckIcon className="mt-0.5 text-green-500 flex-shrink-0" />
                {perk}
              </li>
            ))}
          </ul>
        </div>

        {nextRow && (
          <div className="rounded-2xl border border-amber-200 bg-amber-50/30 p-6 dark:border-amber-900/50 dark:bg-amber-500/[0.04]">
            <h3 className="text-sm font-semibold text-gray-900 dark:text-white mb-4">
              O que desbloqueia em {nextRow.label}
            </h3>
            <ul className="space-y-2">
              {TIER_PERKS[nextRow.code].map((perk) => (
                <li
                  key={perk}
                  className="flex items-start gap-2 text-sm text-gray-700 dark:text-gray-300"
                >
                  <ChevronRightIcon className="mt-0.5 text-amber-600 dark:text-amber-400 flex-shrink-0" />
                  {perk}
                </li>
              ))}
            </ul>
          </div>
        )}
      </section>

      {/* PIONEIRO CALLOUT */}
      {tier.isFoundingSeller && tier.foundingNumber != null && (
        <section className="rounded-2xl border border-brand-200 bg-gradient-to-br from-brand-50 to-white p-6 dark:border-brand-500/30 dark:from-brand-500/10 dark:to-transparent">
          <div className="flex items-center gap-3 mb-3">
            <FoundingBadge
              number={tier.foundingNumber}
              size="md"
              asLink={false}
            />
          </div>
          <p className="text-sm text-gray-700 dark:text-gray-300">
            Você é um dos primeiros sellers do ecossistema Fellow Pay.
            Reconhecemos seu pioneirismo com benefícios exclusivos, prioridade
            no roadmap e acesso direto à fundação. <strong>Pioneiro é vitalício</strong>{" "}
            — não depende do seu volume atual.
          </p>
        </section>
      )}
    </div>
  );
}
