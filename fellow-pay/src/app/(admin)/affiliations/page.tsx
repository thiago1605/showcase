"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { marketplaceService } from "@/services/marketplace.service";
import type { AffiliateMiniStats } from "@/services/marketplace.service";
import { resolveCheckoutUrl } from "@/lib/url";
import { EmptyChecklistState } from "@/components/marketplace/EmptyChecklistState";
import { PageHeader, PageHeaderButton } from "@/components/ui/PageHeader";
import type { Affiliation, AffiliationStatusCode } from "@/types";

/**
 * Painel do AFILIADO — lista das suas afiliações (pendentes, aprovadas,
 * rejeitadas, revogadas). Pra cada APPROVED, mostra o link único de tracking
 * com botão de copiar para clipboard.
 */

const STATUS_LABEL: Record<AffiliationStatusCode, string> = {
  PENDING: "Aguardando aprovação",
  APPROVED: "Aprovada",
  REJECTED: "Rejeitada",
  REVOKED: "Revogada",
};

const STATUS_CLS: Record<AffiliationStatusCode, string> = {
  PENDING: "bg-warning-50 text-warning-700 dark:bg-warning-500/15 dark:text-warning-400",
  APPROVED: "bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400",
  REJECTED: "bg-error-50 text-error-700 dark:bg-error-500/15 dark:text-error-400",
  REVOKED: "bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-500",
};

function formatBRL(v: number) {
  return v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

const PAGE_SIZE = 20;

export default function AffiliationsPage() {
  const [tab, setTab] = useState<AffiliationStatusCode | "ALL">("APPROVED");
  const [page, setPage] = useState(1);

  const { data, isLoading } = useQuery({
    queryKey: ["affiliations", "mine", { tab, page }],
    queryFn: () =>
      marketplaceService.listMyAffiliations({
        page,
        pageSize: PAGE_SIZE,
        status: tab === "ALL" ? undefined : tab,
      }),
  });

  // Mini-stats unificadas para todas as afiliações do seller — render inline
  // nas rows. Cache 60s pq o backend faz N queries de agregação por trás
  // (~1 por afiliação) — vale repetir só quando expira ou em refetch manual.
  const { data: miniStats } = useQuery({
    queryKey: ["affiliations", "mine", "mini-stats"],
    queryFn: () => marketplaceService.getMyAffiliationMiniStats(),
    staleTime: 60_000,
  });
  const statsByAffiliation = useMemo(() => {
    const map = new Map<string, typeof miniStats extends (infer T)[] | undefined ? T : never>();
    miniStats?.forEach((s) => map.set(s.affiliationId, s));
    return map;
  }, [miniStats]);

  const items = data?.items ?? [];
  const totalCount = data?.totalCount ?? 0;
  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Minhas afiliações"
        subtitle="Produtos que você promove. Copie o link de tracking para divulgar — cada venda atribuída paga sua comissão automaticamente."
        actions={
          <PageHeaderButton href="/affiliate-marketplace">
            Encontrar produtos
          </PageHeaderButton>
        }
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <circle cx="9" cy="7" r="4" />
            <path d="M3 21v-2a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v2" />
            <circle cx="17" cy="7" r="3" />
            <path d="M21 21v-2a4 4 0 0 0-3-3.87" />
          </svg>
        }
      />

      <div className="flex items-center gap-1 border-b border-gray-200 dark:border-gray-800">
        {(["APPROVED", "PENDING", "REJECTED", "REVOKED", "ALL"] as const).map((t) => (
          <button
            key={t}
            onClick={() => { setTab(t); setPage(1); }}
            className={`h-10 px-4 text-sm font-medium border-b-2 -mb-px transition-colors ${
              tab === t
                ? "border-brand-500 text-brand-600 dark:text-brand-400"
                : "border-transparent text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-300"
            }`}
          >
            {t === "ALL" ? "Todas" : STATUS_LABEL[t]}
          </button>
        ))}
      </div>

      {isLoading ? (
        <div className="text-sm text-gray-500">Carregando...</div>
      ) : items.length === 0 ? (
        <EmptyTab tab={tab} />
      ) : (
        <>
          <ul className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800 overflow-hidden">
            {items.map((a) => (
              <AffiliationRow key={a.id} a={a} miniStats={statsByAffiliation.get(a.id)} />
            ))}
          </ul>
          {totalPages > 1 && (
            <div className="flex items-center justify-between">
              <button onClick={() => setPage(Math.max(1, page - 1))} disabled={page === 1} className="h-9 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 disabled:opacity-50">← Anterior</button>
              <span className="text-sm text-gray-500 dark:text-gray-400">Página {page} de {totalPages}</span>
              <button onClick={() => setPage(Math.min(totalPages, page + 1))} disabled={page === totalPages} className="h-9 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 disabled:opacity-50">Próxima →</button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

function AffiliationRow({ a, miniStats }: { a: Affiliation; miniStats?: AffiliateMiniStats }) {
  const [copied, setCopied] = useState(false);
  const commissionAmount =
    a.productPrice != null
      ? (a.productPrice * a.effectiveCommissionPercent) / 100
      : null;

  async function copyLink() {
    const url = resolveCheckoutUrl(a.checkoutUrl);
    if (!url) return;
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard pode estar bloqueado em contextos não-HTTPS */
    }
  }

  return (
    // Row inteira é um Link clicável para detalhe (stats + métricas). Mesmo
    // padrão da page de products: overlay absolute Link em z-0, conteúdo em
    // z-10 com pointer-events-none, botão de copiar em pointer-events-auto.
    <li className="group relative">
      <Link
        href={`/affiliations/${a.id}`}
        className="absolute inset-0 z-0"
        aria-label={`Ver métricas de ${a.productName ?? "afiliação"}`}
      />
      <div className="relative z-10 flex items-start justify-between gap-4 px-5 py-4 pointer-events-none">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2 mb-1 flex-wrap">
            <p className="text-sm font-semibold text-gray-900 dark:text-white group-hover:text-brand-600 dark:group-hover:text-brand-400 transition-colors">
              {a.productName ?? "—"}
            </p>
            <span className={`inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-bold ${STATUS_CLS[a.status]}`}>
              {STATUS_LABEL[a.status]}
            </span>
          </div>
          <div className="flex items-baseline gap-4 text-xs text-gray-500 dark:text-gray-400 flex-wrap">
            {a.productPrice != null && (
              <span>
                Ticket{" "}
                <span className="font-medium text-gray-700 dark:text-gray-300 tabular-nums">
                  {formatBRL(a.productPrice)}
                </span>
              </span>
            )}
            <span>
              Comissão{" "}
              <span className="font-semibold text-brand-600 dark:text-brand-400 tabular-nums">
                {a.effectiveCommissionPercent.toFixed(1)}%
              </span>
              {commissionAmount != null && (
                <span className="text-gray-500 dark:text-gray-400">
                  {" · "}
                  <span className="tabular-nums">{formatBRL(commissionAmount)}</span> / venda
                </span>
              )}
            </span>
          </div>
          {a.rejectedReason && (
            <p className="mt-1 text-xs text-error-600 dark:text-error-400">
              Motivo: {a.rejectedReason}
            </p>
          )}
          {/* Mini-stats 30d inline — só renderiza se afiliação APPROVED + stats
              chegaram do backend. Pra Pending/Rejected/Revoked, stats não fazem
              sentido (sem tracking ativo). Layout compacto: 3 valores tabulares
              separados por bullet. */}
          {a.status === "APPROVED" && miniStats && (
            <div className="mt-2 flex items-baseline gap-4 text-[11px] text-gray-500 dark:text-gray-400 flex-wrap">
              <span>
                <span className={`font-semibold tabular-nums ${miniStats.clicks30d > 0 ? "text-gray-700 dark:text-gray-300" : ""}`}>
                  {miniStats.clicks30d}
                </span>{" "}
                cliques (30d)
              </span>
              <span className="text-gray-300 dark:text-gray-700">·</span>
              <span>
                <span className={`font-semibold tabular-nums ${miniStats.sales30d > 0 ? "text-success-600 dark:text-success-400" : ""}`}>
                  {miniStats.sales30d}
                </span>{" "}
                vendas
              </span>
              <span className="text-gray-300 dark:text-gray-700">·</span>
              <span>
                <span className={`font-semibold tabular-nums ${miniStats.earnings30d > 0 ? "text-success-600 dark:text-success-400" : ""}`}>
                  {formatBRL(miniStats.earnings30d)}
                </span>{" "}
                ganhos
              </span>
            </div>
          )}
        </div>

        {a.status === "APPROVED" && a.checkoutUrl && (
          // pointer-events-auto: re-habilita o botão de copiar para não
          // disparar a navegação do row.
          <div className="flex flex-col items-end gap-1 shrink-0 pointer-events-auto">
            <button
              onClick={copyLink}
              className="h-8 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-xs font-medium text-white"
            >
              {copied ? "Copiado ✓" : "Copiar link"}
            </button>
            <span
              className="font-mono text-[10px] text-gray-500 dark:text-gray-400 max-w-[300px] truncate"
              title={resolveCheckoutUrl(a.checkoutUrl)}
            >
              {resolveCheckoutUrl(a.checkoutUrl)}
            </span>
          </div>
        )}
        <svg
          width="16" height="16" viewBox="0 0 24 24" fill="none"
          stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"
          aria-hidden="true"
          className="self-center text-gray-300 dark:text-gray-600 group-hover:text-gray-500 dark:group-hover:text-gray-400 transition-colors shrink-0"
        >
          <polyline points="9 18 15 12 9 6" />
        </svg>
      </div>
    </li>
  );
}

function EmptyTab({ tab }: { tab: AffiliationStatusCode | "ALL" }) {
  // Empty state com checklist só faz sentido para "ALL" (= afiliado novo,
  // sem nenhuma afiliação). Pras outras tabs (APPROVED/PENDING/REJECTED/REVOKED
  // vazias), o user já tem afiliações em OUTRO status — não precisa de
  // onboarding, só de feedback simples.
  if (tab !== "ALL") {
    return (
      <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 p-10 text-center">
        <p className="text-sm font-medium text-gray-700 dark:text-gray-300">
          {tab === "APPROVED"
            ? "Nenhuma afiliação aprovada"
            : tab === "PENDING"
              ? "Nenhuma solicitação aguardando"
              : tab === "REJECTED"
                ? "Nenhuma rejeitada"
                : "Nenhuma revogada"}
        </p>
      </div>
    );
  }

  return (
    <EmptyChecklistState
      title="Você ainda não é afiliado de nenhum produto"
      subtitle="Vire afiliado para ganhar comissão promovendo produtos de outros sellers."
      steps={[
        {
          icon: "🔍",
          title: "Explore o catálogo",
          description: "Veja todos os produtos abertos para afiliação no seu tenant.",
          href: "/affiliate-marketplace",
          cta: "Ir para o catálogo",
        },
        {
          icon: "✋",
          title: "Solicite afiliação",
          description: "Clica no produto que combina com seu público. Produtor aprova (ou auto-aprova se aberto).",
        },
        {
          icon: "🔗",
          title: "Compartilhe seu link",
          description: "Após aprovado, você recebe um link único para divulgar nas suas redes.",
        },
        {
          icon: "💰",
          title: "Ganhe comissão",
          description: "Cada venda atribuída ao seu link paga sua comissão automaticamente.",
        },
      ]}
    />
  );
}
