"use client";

import { useState } from "react";
import Link from "next/link";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { marketplaceService } from "@/services/marketplace.service";
import { Select } from "@/components/ui/Select";
import { Sparkline } from "@/components/dashboard/Sparkline";
import { EmptyChecklistState } from "@/components/marketplace/EmptyChecklistState";
import { PeriodPicker, type PeriodDays } from "@/components/marketplace/PeriodPicker";
import { PageHeader, PageHeaderButton } from "@/components/ui/PageHeader";
import type { Product, ProductOwnerStats, ProductStatusCode } from "@/types";

const STATUS_LABEL: Record<ProductStatusCode, string> = {
  DRAFT: "Rascunho",
  PUBLISHED: "Publicado",
  PAUSED: "Pausado",
  ARCHIVED: "Arquivado",
};

const STATUS_CLS: Record<ProductStatusCode, string> = {
  DRAFT: "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300",
  PUBLISHED: "bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400",
  PAUSED: "bg-warning-50 text-warning-700 dark:bg-warning-500/15 dark:text-warning-400",
  ARCHIVED: "bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-500",
};

const TYPE_LABEL: Record<string, string> = {
  DIGITAL: "Digital",
  PHYSICAL: "Físico",
  SERVICE: "Serviço",
};

const MODE_LABEL: Record<string, string> = {
  OPEN: "Aberto",
  REQUEST: "Sob pedido",
  CLOSED: "Fechado",
};

function formatBRL(v: number): string {
  return v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

/**
 * Formato compacto para inline metrics — "R$ 1,2k" / "R$ 12,5k" / "R$ 1,3M".
 * Mantém R$ para ficar claro que é monetário, mas economiza espaço (Vendas 30d
 * + Volume 30d + Afiliados em uma linha precisa caber sem wrap em mobile).
 */
function formatBRLCompact(v: number): string {
  if (v < 1000) return `R$ ${v.toFixed(0)}`;
  if (v < 1_000_000) return `R$ ${(v / 1000).toFixed(v < 10_000 ? 1 : 0).replace(".", ",")}k`;
  return `R$ ${(v / 1_000_000).toFixed(1).replace(".", ",")}M`;
}

const PAGE_SIZE = 20;

export default function ProductsPage() {
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState<ProductStatusCode | "">("");
  const queryClient = useQueryClient();

  const { data, isLoading } = useQuery({
    queryKey: ["products", "list", { page, statusFilter }],
    queryFn: () =>
      marketplaceService.listMyProducts({
        page,
        pageSize: PAGE_SIZE,
        status: statusFilter || undefined,
      }),
  });

  // Stats card no topo — query separada (não muda com filtro/paginação).
  // staleTime 30s para evitar refetch a cada click; refetchOnWindowFocus
  // mantém fresh quando o usuário volta da aba.
  //
  // retry: 1 para não amarrar 4 tentativas com backoff antes de aceitar que o
  // endpoint não tá disponível (cenário: backend antigo rodando, novo deploy
  // ainda não subiu). Sem isso, o skeleton fica permanente por uns 10s+ até
  // o TanStack desistir e zerar `data` definitivamente.
  const [periodDays, setPeriodDays] = useState<PeriodDays>(30);
  const { data: stats, isLoading: statsLoading, isError: statsError } = useQuery({
    queryKey: ["products", "stats", periodDays],
    queryFn: () => marketplaceService.getMyProductStats(periodDays),
    staleTime: 30_000,
    retry: 1,
  });

  const publish = useMutation({
    mutationFn: (id: string) => marketplaceService.publishProduct(id),
    onSuccess: () => {
      // Invalida list + stats — mudança de status muda contagem dos cards.
      queryClient.invalidateQueries({ queryKey: ["products"] });
    },
  });
  const pause = useMutation({
    mutationFn: (id: string) => marketplaceService.pauseProduct(id),
    onSuccess: () => {
      // Invalida list + stats — mudança de status muda contagem dos cards.
      queryClient.invalidateQueries({ queryKey: ["products"] });
    },
  });
  const resume = useMutation({
    mutationFn: (id: string) => marketplaceService.resumeProduct(id),
    onSuccess: () => {
      // Invalida list + stats — mudança de status muda contagem dos cards.
      queryClient.invalidateQueries({ queryKey: ["products"] });
    },
  });

  const items = data?.items ?? [];
  const totalCount = data?.totalCount ?? 0;
  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Meus produtos"
        subtitle="Produtos que você criou. Configure comissão de afiliação e abra ou feche para parceiros promoverem."
        actions={<PageHeaderButton href="/products/new">+ Novo produto</PageHeaderButton>}
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
            <polyline points="3.27 6.96 12 12.01 20.73 6.96" />
            <line x1="12" y1="22.08" x2="12" y2="12" />
          </svg>
        }
      />

      {/* Cards só renderizam se a query carregou OU está carregando. Em erro
          (ex: backend antigo sem o endpoint /stats), oculta — não vale mostrar
          UI vazia se não tem dado. Resto da página segue funcionando. */}
      {!statsError && (
        <div className="space-y-3">
          <div className="flex items-center justify-between flex-wrap gap-2">
            <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400">
              Painel · últimos {periodDays} dias
            </p>
            <PeriodPicker value={periodDays} onChange={setPeriodDays} />
          </div>
          <StatsCards stats={stats} loading={statsLoading} periodDays={periodDays} />
        </div>
      )}

      <div className="flex items-center gap-3">
        {/* Select custom do design system (`components/ui/Select.tsx`) em vez
            do <select> nativo. O native usa o popup do SO/browser para renderizar
            as opções — visual completamente fora do padrão (preto flutuante no
            macOS, branco hard-edge no Windows). O custom renderiza um <ul> com
            as mesmas classes do resto dos dropdowns. */}
        <Select
          ariaLabel="Filtrar por status"
          className="w-52"
          value={statusFilter}
          onChange={(v) => {
            setStatusFilter(v as ProductStatusCode | "");
            setPage(1);
          }}
          options={[
            { value: "", label: "Todos os status" },
            { value: "DRAFT", label: "Rascunho" },
            { value: "PUBLISHED", label: "Publicado" },
            { value: "PAUSED", label: "Pausado" },
            { value: "ARCHIVED", label: "Arquivado" },
          ]}
        />
        <span className="text-sm text-gray-500 dark:text-gray-400 tabular-nums">
          {totalCount} {totalCount === 1 ? "produto" : "produtos"}
        </span>
      </div>

      {isLoading ? (
        <Skeleton />
      ) : items.length === 0 ? (
        <EmptyBox />
      ) : (
        <>
          <ul className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800 overflow-hidden">
            {items.map((p) => (
              <ProductRow
                key={p.id}
                product={p}
                onPublish={() => publish.mutate(p.id)}
                onPause={() => pause.mutate(p.id)}
                onResume={() => resume.mutate(p.id)}
              />
            ))}
          </ul>
          {totalPages > 1 && (
            <div className="flex items-center justify-between">
              <button
                onClick={() => setPage(Math.max(1, page - 1))}
                disabled={page === 1}
                className="h-9 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-50"
              >
                ← Anterior
              </button>
              <span className="text-sm text-gray-500 dark:text-gray-400">
                Página {page} de {totalPages}
              </span>
              <button
                onClick={() => setPage(Math.min(totalPages, page + 1))}
                disabled={page === totalPages}
                className="h-9 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-50"
              >
                Próxima →
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

function ProductRow({
  product,
  onPublish,
  onPause,
  onResume,
}: {
  product: Product;
  onPublish: () => void;
  onPause: () => void;
  onResume: () => void;
}) {
  // Row inteira é clicável (cursor pointer + hover) — UX padrão de tabela
  // admin. Padrão usado:
  //   - <Link absolute inset-0 z-0> cobre toda a área da row, captura clique
  //     em qualquer lugar "vazio" (thumbnail, título, badges, metadata).
  //   - Conteúdo visual fica em <div pointer-events-none>, então cliques no
  //     texto/badge passam direto para Link sem precisar de stopPropagation.
  //   - Botões de ação dentro de <div pointer-events-auto> reativam o pointer
  //     events só para eles — captura o clique antes de chegar no Link
  //     (são z-10 vs z-0). Sem precisar de stopPropagation/preventDefault.
  // Removido o botão "Detalhes" redundante: a row inteira já leva para lá.
  // O chevron pequeno à direita serve de affordance visual.
  return (
    <li className="group relative">
      <Link
        href={`/products/${product.id}`}
        className="absolute inset-0 z-0 rounded-none"
        aria-label={`Abrir detalhes de ${product.name}`}
      />
      {/* Sem hover bg na row — affordance de clique fica no chevron + cor do
          título mudando. Hover de background virava "selecionado" visualmente,
          o que confunde quando o usuário só está movendo o mouse. */}
      <div className="relative z-10 flex items-start gap-4 px-5 py-4 pointer-events-none">
        {/* Thumbnail 16:9 ~120×68 — proporção real do card + ressonância com
            o que o produtor configurou no upload (aspect 16:9). Antes era
            64×64 (ratio 1:1) — virava ícone, perdia leitura da capa. */}
        <div className="w-[120px] aspect-[16/9] shrink-0 rounded-lg bg-gradient-to-br from-gray-100 to-gray-200 dark:from-gray-800 dark:to-gray-900 overflow-hidden flex items-center justify-center text-gray-400">
          {product.coverImageUrl ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img src={product.coverImageUrl} alt={product.name} className="w-full h-full object-cover" />
          ) : (
            <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
              <rect x="3" y="3" width="18" height="18" rx="2" />
              <circle cx="9" cy="9" r="2" />
              <path d="m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21" />
            </svg>
          )}
        </div>

        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2 flex-wrap mb-1">
            <span className="text-sm font-semibold text-gray-900 dark:text-white group-hover:text-brand-600 dark:group-hover:text-brand-400 transition-colors">
              {product.name}
            </span>
            <span
              className={`inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-bold ${STATUS_CLS[product.status]}`}
            >
              {STATUS_LABEL[product.status]}
            </span>
            <span className="inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-medium bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400">
              {TYPE_LABEL[product.type]}
            </span>
          </div>
          <div className="flex items-baseline gap-4 text-xs text-gray-500 dark:text-gray-400 flex-wrap">
            <span className="tabular-nums font-semibold text-gray-700 dark:text-gray-300">
              {formatBRL(product.price)}
            </span>
            <span>
              Comissão{" "}
              <span className="font-medium text-gray-700 dark:text-gray-300 tabular-nums">
                {product.defaultAffiliateCommissionPercent.toFixed(1)}%
              </span>
            </span>
            <span>
              Afiliação{" "}
              <span className="font-medium text-gray-700 dark:text-gray-300">
                {MODE_LABEL[product.affiliationMode]}
              </span>
            </span>
            {product.category && <span>{product.category}</span>}
          </div>

          {/* Métricas por produto + sparkline. Sparkline visualiza vendas dos
              últimos 30d (sem chart, ficaria zerado dificil de diferenciar
              "0 nesse mês" vs "0 desde sempre"). Compacto: 4 métricas inline +
              gráfico 80×24px. Sem dado de venda (salesInPeriod == 0) suprime o
              sparkline para evitar barra plana sem sinal. */}
          {product.metrics && (
            <div className="flex items-center gap-4 text-[11px] text-gray-500 dark:text-gray-400 mt-2 pt-2 border-t border-gray-100 dark:border-gray-800/50">
              <Metric
                label="Vendas 30d"
                value={product.metrics.sales30d.toString()}
                accent={product.metrics.sales30d > 0}
              />
              <Metric
                label="Faturamento 30d"
                value={formatBRLCompact(product.metrics.volume30d)}
                accent={product.metrics.volume30d > 0}
              />
              <Metric
                label="Afiliados"
                value={product.metrics.activeAffiliates.toString()}
                accent={product.metrics.activeAffiliates > 0}
              />
              {product.metrics.sales30d > 0 && product.metrics.salesByDay && (
                <div className="ml-auto w-24 -my-1">
                  <Sparkline
                    data={product.metrics.salesByDay}
                    color="#16a34a"
                    height={24}
                    ariaLabel={`Vendas de ${product.name} nos últimos 30 dias`}
                  />
                </div>
              )}
            </div>
          )}
        </div>

        {/* pointer-events-auto: re-habilita interação só nos botões. Cliques
            aqui são capturados pelos botões (z-10) e não chegam no Link (z-0). */}
        <div className="flex items-center gap-2 shrink-0 pointer-events-auto">
          {product.status === "DRAFT" && (
            <button
              onClick={onPublish}
              className="h-8 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-xs font-medium text-white"
            >
              Publicar
            </button>
          )}
          {product.status === "PUBLISHED" && (
            <button
              onClick={onPause}
              className="h-8 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800"
            >
              Pausar
            </button>
          )}
          {product.status === "PAUSED" && (
            <button
              onClick={onResume}
              className="h-8 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-xs font-medium text-white"
            >
              Retomar
            </button>
          )}
          {/* Abrir checkout público — só faz sentido se PUBLISHED ou PAUSED
              (DRAFT ainda não tem URL ativa, ARCHIVED não converte). */}
          {(product.status === "PUBLISHED" || product.status === "PAUSED") && (
            <a
              href={`/p/${product.slug}`}
              target="_blank"
              rel="noopener noreferrer"
              title="Abrir checkout público em nova aba"
              className="h-8 inline-flex items-center gap-1 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-3 text-xs font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800"
            >
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
                <polyline points="15 3 21 3 21 9" />
                <line x1="10" y1="14" x2="21" y2="3" />
              </svg>
              Ver checkout
            </a>
          )}
          {/* Chevron — affordance visual de "row clicável". Pointer-events-none
              para não capturar clique, deixa o Link de fundo receber. */}
          <svg
            width="16"
            height="16"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
            className="text-gray-300 dark:text-gray-600 group-hover:text-gray-500 dark:group-hover:text-gray-400 transition-colors pointer-events-none ml-1"
          >
            <polyline points="9 18 15 12 9 6" />
          </svg>
        </div>
      </div>
    </li>
  );
}

/**
 * Cards de resumo no topo da página — viram o painel de "página de config" em
 * "painel de marketplace". 4 KPIs: Total (com breakdown), Publicados, Vendas
 * 30d, Volume 30d. Loading state mantém o layout (skeletons) para evitar shift.
 *
 * Não mostra "Comissões pagas" ainda — agregação envolve SplitTransfer com
 * filtro por produtor da TX (não trivial) e fica como tech debt para próxima
 * fase. Decisão: 4 cards conhecidos > 5 cards com 1 placeholder.
 */
function StatsCards({
  stats,
  loading,
  periodDays,
}: {
  stats: ProductOwnerStats | undefined;
  loading: boolean;
  periodDays: number;
}) {
  // Default values para renderizar zeros quando backend retorna para seller novo
  // sem produtos. Mantém o layout — evita CLS quando os dados chegam.
  const safe = stats ?? {
    totalProducts: 0,
    publishedCount: 0,
    draftCount: 0,
    pausedCount: 0,
    salesInPeriod: 0,
    volumeInPeriod: 0,
    previousSalesInPeriod: 0,
    previousVolumeInPeriod: 0,
    salesByDay: new Array(30).fill(0) as number[],
    volumeByDay: new Array(30).fill(0) as number[],
    commissionsPaidInPeriod: 0,
    previousCommissionsPaidInPeriod: 0,
  };
  return (
    // Grid responsivo escalonado: 2 cols mobile → 3 cols md (1280px+) → 5
    // cols xl (1536px+). Antes era lg:grid-cols-5 que estourava em laptops
    // de 13"/14" com sidebar (~1100-1200px de content area), quebrando
    // labels longos como "FATURAMENTO (30D)" e "COMISSÕES PAGAS (30D)" em
    // 2 linhas. Sufixo "(Nd)" dos labels removido — o PeriodPicker acima
    // já estabelece o contexto temporal, repetir é redundante e custa width.
    <div className="grid grid-cols-2 md:grid-cols-3 xl:grid-cols-5 gap-3">
      <StatCard
        label="Total de produtos"
        value={loading ? null : safe.totalProducts.toString()}
        sub={
          loading
            ? null
            : `${safe.publishedCount} pub · ${safe.draftCount} draft · ${safe.pausedCount} pause`
        }
      />
      <StatCard
        label="Publicados"
        value={loading ? null : safe.publishedCount.toString()}
        accent={!loading && safe.publishedCount > 0}
      />
      <StatCard
        label="Vendas"
        value={loading ? null : safe.salesInPeriod.toString()}
        previous={safe.previousSalesInPeriod}
        current={safe.salesInPeriod}
        sparkData={safe.salesByDay}
        sparkColor="#16a34a"
        accent={!loading && safe.salesInPeriod > 0}
      />
      <StatCard
        label="Faturamento"
        value={loading ? null : formatBRL(safe.volumeInPeriod)}
        previous={safe.previousVolumeInPeriod}
        current={safe.volumeInPeriod}
        sparkData={safe.volumeByDay.map((v) => Number(v))}
        accent={!loading && safe.volumeInPeriod > 0}
      />
      <StatCard
        label="Comissões pagas"
        value={loading ? null : formatBRL(safe.commissionsPaidInPeriod)}
        previous={safe.previousCommissionsPaidInPeriod}
        current={safe.commissionsPaidInPeriod}
        sub={
          loading
            ? null
            : safe.commissionsPaidInPeriod > 0 && safe.volumeInPeriod > 0
              ? `${((safe.commissionsPaidInPeriod / safe.volumeInPeriod) * 100).toFixed(1)}% do faturamento`
              : "Pago a afiliados + co-producers"
        }
      />
    </div>
  );
}

function StatCard({
  label,
  value,
  sub,
  accent,
  previous,
  current,
  sparkData,
  sparkColor,
}: {
  label: string;
  value: string | null;
  sub?: string | null;
  accent?: boolean;
  /** Pra delta badge: valor da janela anterior. Opcional — sem ele, não mostra badge. */
  previous?: number;
  current?: number;
  /** Pra sparkline inferior: 30 ints/decimals. Opcional. */
  sparkData?: number[];
  sparkColor?: string;
}) {
  // Delta% com tratamentos para zero anterior (vira "novo" em vez de divisão por zero).
  let delta: number | "new" | null = null;
  if (previous !== undefined && current !== undefined) {
    if (previous === 0 && current === 0) delta = 0;
    else if (previous === 0) delta = "new";
    else delta = ((current - previous) / previous) * 100;
  }

  // Padrão dos data cards do dashboard (DashboardMetrics / SellerBalanceCard /
  // PendingFundsCard): border-gray-200/80, dark:bg-gray-900, p-5, label
  // `text-[10px] uppercase tracking-[0.06em]`, valor `text-[22px] font-semibold`.
  return (
    <div className="rounded-2xl border border-gray-200/80 bg-white p-5 dark:border-gray-800 dark:bg-gray-900 flex flex-col gap-2 min-h-[140px]">
      <div className="flex items-center justify-between">
        <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400">
          {label}
        </p>
        {delta !== null && value !== null && (
          delta === "new" ? (
            <span className="inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-semibold bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400">
              novo
            </span>
          ) : (
            <span className={`inline-flex items-center px-1.5 py-0.5 rounded text-[10px] font-semibold tabular-nums ${
              delta > 0 ? "bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400" :
              delta < 0 ? "bg-error-50 text-error-700 dark:bg-error-500/15 dark:text-error-400" :
              "bg-gray-100 text-gray-500 dark:bg-gray-800 dark:text-gray-400"
            }`}>
              {delta > 0 ? "+" : ""}{delta.toFixed(0)}%
            </span>
          )
        )}
      </div>
      {value === null ? (
        <div className="h-7 w-20 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />
      ) : (
        <p
          className={`text-[22px] leading-[1.15] font-semibold tabular-nums tracking-tight whitespace-nowrap ${
            accent
              ? "text-brand-600 dark:text-brand-400"
              : "text-gray-900 dark:text-white"
          }`}
        >
          {value}
        </p>
      )}
      {sub && (
        <p className="text-[11px] text-gray-500 dark:text-gray-400 tabular-nums">
          {sub}
        </p>
      )}
      {sparkData && sparkData.length > 1 && (
        <div className="-mx-2 -mb-2 mt-auto">
          <Sparkline data={sparkData} color={sparkColor ?? "#b026ff"} height={32} ariaLabel={`${label} nos últimos 30 dias`} />
        </div>
      )}
    </div>
  );
}

/**
 * Métrica inline na row (compact, labels embaixo do valor). Accent verde quando
 * tem atividade — sinal visual rápido pro produtor escanear a lista e ver
 * quais produtos estão "rolando" sem precisar abrir cada um.
 */
function Metric({
  label,
  value,
  accent,
}: {
  label: string;
  value: string;
  accent?: boolean;
}) {
  return (
    <div className="flex items-baseline gap-1.5">
      <span
        className={`tabular-nums font-semibold ${
          accent
            ? "text-success-600 dark:text-success-400"
            : "text-gray-600 dark:text-gray-400"
        }`}
      >
        {value}
      </span>
      <span className="text-gray-400 dark:text-gray-500">{label}</span>
    </div>
  );
}

function Skeleton() {
  return (
    <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800 overflow-hidden">
      {[0, 1, 2].map((i) => (
        <div key={i} className="flex items-start gap-4 px-5 py-4 animate-pulse">
          {/* Match real thumbnail: 120×68 (aspect 16:9), não 64×64 */}
          <div className="w-[120px] aspect-[16/9] shrink-0 bg-gray-200 dark:bg-gray-700 rounded-lg" />
          <div className="flex-1 space-y-2 pt-1">
            <div className="h-4 w-1/2 bg-gray-200 dark:bg-gray-700 rounded" />
            <div className="h-3 w-2/3 bg-gray-100 dark:bg-gray-800 rounded" />
          </div>
        </div>
      ))}
    </div>
  );
}

function EmptyBox() {
  // Checklist de onboarding para produtor que acabou de chegar. Steps refletem
  // o caminho de criação → publicação → afiliação. Conforme cada etapa for
  // concluída, o `done` viraria true (lógica de detecção fica fora do escopo
  // — pode evoluir lendo o estado real de cada step).
  return (
    <EmptyChecklistState
      title="Crie seu primeiro produto"
      subtitle="Em 5 minutos você pode estar vendendo com afiliados promovendo para você."
      steps={[
        {
          icon: "🛍",
          title: "Criar produto",
          description: "Nome, preço, descrição e link de entrega — o básico para abrir a venda.",
          href: "/products/new",
          cta: "Criar agora",
        },
        {
          icon: "🚀",
          title: "Publicar no marketplace",
          description: "Produto sai do DRAFT e fica visível para afiliados se inscreverem.",
        },
        {
          icon: "👥",
          title: "Aprovar afiliados",
          description: "Sob pedido você aprova; aberta auto-aprova todos. Configure no produto.",
        },
        {
          icon: "📈",
          title: "Acompanhar vendas",
          description: "Stats por produto + leaderboard de top afiliados aparece aqui.",
        },
      ]}
    />
  );
}
