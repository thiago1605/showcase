"use client";

import { use, useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { marketplaceService } from "@/services/marketplace.service";
import { getCurrentSellerId } from "@/context/AuthContext";
import { resolveCheckoutUrl } from "@/lib/url";
import { Sparkline } from "@/components/dashboard/Sparkline";
import { PeriodPicker, type PeriodDays } from "@/components/marketplace/PeriodPicker";
import { BackLink } from "@/components/ui/BackLink";
import { ConfirmModal } from "@/components/ui/ConfirmModal";
import type { AffiliationStatusCode } from "@/types";

/**
 * Página de detalhe da afiliação — abre quando o afiliado clica numa row em
 * `/affiliations`. Centraliza:
 *   - Info do produto + status da afiliação
 *   - Link de divulgação com copy-to-clipboard
 *   - Métricas de performance (TPV, ganhos, vendas) em 2 janelas (30d / all-time)
 *   - Saldo pendente (splits ainda não liberados para carteira)
 *
 * Acessível pelo afiliado dono OU pelo produtor do produto (backend authz).
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

function formatBRL(v: number): string {
  return v.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

function formatBRLCompact(v: number): string {
  if (v < 1000) return `R$ ${v.toFixed(0)}`;
  if (v < 1_000_000) return `R$ ${(v / 1000).toFixed(v < 10_000 ? 1 : 0).replace(".", ",")}k`;
  return `R$ ${(v / 1_000_000).toFixed(1).replace(".", ",")}M`;
}

export default function AffiliationDetailPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = use(params);

  // Resolve via endpoint direto. Backend autoriza tanto o afiliado dono
  // quanto o produtor do produto (item de auth no service). Pra outros
  // sellers do tenant, retorna 404 — query erra e cai no estado "não
  // encontrada" abaixo. retry: 1 para não amarrar 4 tentativas com backoff
  // em rota inválida.
  const {
    data: affiliation,
    isLoading: loadingList,
    error: loadError,
  } = useQuery({
    queryKey: ["affiliation", id],
    queryFn: () => marketplaceService.getAffiliation(id),
    retry: 1,
  });

  // Período selecionado pelo user — 30d default. Vai para queryKey para cache
  // separado por janela; trocar de 30 para 7 não usa data stale do 30.
  const [periodDays, setPeriodDays] = useState<PeriodDays>(30);

  const { data: stats, isLoading: loadingStats, error: statsError } = useQuery({
    queryKey: ["affiliations", id, "stats", periodDays],
    queryFn: () => marketplaceService.getAffiliationStats(id, periodDays),
    enabled: !!affiliation,
    staleTime: 30_000,
  });

  const [copied, setCopied] = useState(false);
  async function copyLink() {
    const url = resolveCheckoutUrl(affiliation?.checkoutUrl);
    if (!url) return;
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard pode estar bloqueado em contextos não-HTTPS */
    }
  }

  // Revoke flow — encerra a afiliação. Backend autoriza tanto o afiliado
  // (self-leave) quanto o produtor (revogar para parar o link de converter).
  // ConfirmModal para evitar click acidental (destructive action). Invalida a
  // query do detail + da listagem para refletir o novo status REVOKED.
  const queryClient = useQueryClient();
  const [confirmingRevoke, setConfirmingRevoke] = useState(false);
  const revoke = useMutation({
    mutationFn: () => marketplaceService.revokeAffiliation(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["affiliation", id] });
      queryClient.invalidateQueries({ queryKey: ["affiliations"] });
      setConfirmingRevoke(false);
    },
  });

  if (loadingList) {
    return (
      <div className="space-y-4">
        <div className="h-6 w-1/3 bg-gray-200 dark:bg-gray-800 rounded animate-pulse" />
        <div className="h-32 bg-gray-100 dark:bg-gray-900 rounded-2xl animate-pulse" />
      </div>
    );
  }

  if (!affiliation) {
    // Tanto 404 (afiliação inexistente) quanto 403 silencioso (seller que
    // não é nem o afiliado nem o produtor) caem aqui — backend devolve null
    // em ambos os casos para evitar enumeration. UX explica os dois cenários
    // sem expor qual foi o motivo.
    return (
      <div className="rounded-2xl border border-gray-200 dark:border-gray-800 bg-white dark:bg-white/[0.03] p-8 text-center">
        <p className="text-base font-medium text-gray-800 dark:text-gray-200">
          Afiliação não encontrada
        </p>
        <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
          {loadError instanceof Error
            ? "Verifique o link ou se você tem permissão para ver esta afiliação."
            : "Verifique o link ou volte para a lista."}
        </p>
        <BackLink
          fallbackHref="/affiliations"
          label="Retornar"
          className="mt-4 inline-flex h-10 items-center gap-2 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white"
        />
      </div>
    );
  }

  // Detecta se o viewer é o próprio afiliado ou um terceiro (provavelmente o
  // produtor drillando do leaderboard). Usado para ajustar copy do link de
  // divulgação — "SEU link" só faz sentido quando você É o afiliado. Pra
  // produtor, viramos a copy para "LINK DO AFILIADO".
  const currentSellerId = getCurrentSellerId();
  const viewerIsAffiliate = currentSellerId === affiliation.affiliateSellerId;

  const fullCheckoutUrl = resolveCheckoutUrl(affiliation.checkoutUrl);
  const isApproved = affiliation.status === "APPROVED";
  const hasActivity = !!stats && (stats.clicksInPeriod > 0 || stats.salesInPeriod > 0);

  return (
    <div className="space-y-4 max-w-6xl">
      {/* BackLink acima do hero (não dentro) — fica em página em vez de
          dentro de card, batendo com o mesmo padrão de /products/[id]. */}
      <BackLink fallbackHref="/affiliations" label="Retornar" />

      {/* Hero — produto cover (se houver) + título contextual. Dá identidade
          visual: o afiliado sabe imediatamente QUAL produto ele está promovendo.
          Cover com aspect-[16/9] FIXO (não usar aspect-auto em sm+ pq o flex
          estica a imagem para match a altura da info side). lg:m-4 dá respiro. */}
      <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 overflow-hidden">
        <div className="flex flex-col sm:flex-row sm:items-center">
          {affiliation.productCoverImageUrl && (
            <div className="sm:w-72 shrink-0 aspect-[16/9] overflow-hidden sm:m-4 sm:rounded-xl">
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img
                src={affiliation.productCoverImageUrl}
                alt={affiliation.productName ?? "Produto"}
                className="w-full h-full object-cover"
              />
            </div>
          )}
          <div className="p-6 flex-1 min-w-0">
            <div className="flex items-center gap-2 flex-wrap">
              <span className="text-[10px] uppercase tracking-[0.06em] font-semibold text-gray-500 dark:text-gray-400">
                Afiliação
              </span>
              <span className={`inline-flex items-center px-2 py-0.5 rounded-md text-[10px] uppercase tracking-wider font-semibold ${STATUS_CLS[affiliation.status]}`}>
                {STATUS_LABEL[affiliation.status]}
              </span>
            </div>
            {/* H1: produto em destaque (preto bold), afiliado contextualizado
                em texto menor abaixo. Antes era "{Afiliado} promovendo {Produto}"
                tudo em text-2xl com o nome do afiliado em roxo gigante —
                competia com o nome do produto. Agora hierarquia clara:
                produto primary, afiliado secondary line. */}
            <h1 className="mt-1 text-xl font-semibold text-gray-900 dark:text-white leading-tight">
              {affiliation.productName ?? "Produto"}
            </h1>
            {/* Antes era "Promovido por X" — semanticamente errado: "promovido
                por" em PT sugere patrocínio/endosso, mas afiliado não patrocina,
                ele divulga. Quem promove o produto é o produtor. Agora "Afiliado:"
                deixa o papel de Mansão Wayne explícito (= divulgador, não dono). */}
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
              {viewerIsAffiliate ? "Sua afiliação" : "Afiliado"}
              <span className="mx-1">·</span>
              <span className="font-medium text-brand-600 dark:text-brand-400">
                {affiliation.affiliateSellerName ?? "Afiliado"}
              </span>
            </p>
            {/* Métricas inline sem separators visuais — flex-gap já dá espaço.
                Antes tinha <span>·</span> entre os blocos que ficava flutuando
                no topo (alinhado ao baseline da label, não do conjunto). */}
            {/* Split do ticket por venda — explicitando QUEM ganha o quê.
                Antes mostrava "Comissão 35%" + "Por venda R$ 103,95" sem
                deixar claro se aquele valor era do afiliado ou do produtor.
                Agora cada bloco tem sujeito explícito ("Afiliado", "Produtor"),
                com % na sublabel para contexto. O bloco do próprio viewer
                ganha destaque visual (cor + bold mais forte): success-600
                pro afiliado (verde = ganho), brand-600 pro produtor
                (purple = identidade da plataforma). */}
            {/* Métricas como etiquetas (stickers). Estratégia: stickers
                neutros gray + UM destacado em solid brand-500 — o card que
                representa o ganho do próprio viewer. Cria hierarquia clara:
                "esse aqui é o seu". O Ticket é sempre neutro (referência,
                não ganho de ninguém). */}
            {affiliation.productPrice != null && (() => {
              // Constantes locais — extrair as classes evita verbosidade nos
              // 3 cards. `Highlight` = brand-500 solid + texto branco.
              // `Neutral` = gray-50 + texto gray-900.
              const neutralCard = "rounded-lg bg-gray-50 dark:bg-gray-800/60 px-3.5 py-2.5";
              const neutralLabel = "text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400 mb-1 font-semibold";
              const neutralValue = "text-lg font-bold text-gray-900 dark:text-white tabular-nums leading-none";

              const highlightCard = "rounded-lg bg-brand-500 px-3.5 py-2.5 shadow-sm shadow-brand-500/20";
              const highlightLabel = "text-[10px] uppercase tracking-wider text-white/80 mb-1 font-semibold";
              const highlightValue = "text-lg font-bold text-white tabular-nums leading-none";

              const affiliateHighlighted = viewerIsAffiliate;
              const producerHighlighted = !viewerIsAffiliate;

              return (
                <div className="mt-3 flex flex-wrap gap-2">
                  {/* Ticket — sempre neutro (referência, não ganho de ninguém) */}
                  <div className={neutralCard}>
                    <p className={neutralLabel}>Ticket</p>
                    <p className={neutralValue}>{formatBRL(affiliation.productPrice)}</p>
                  </div>
                  {/* Ganho do afiliado — highlight se o viewer É o afiliado */}
                  <div className={affiliateHighlighted ? highlightCard : neutralCard}>
                    <p className={affiliateHighlighted ? highlightLabel : neutralLabel}>
                      {viewerIsAffiliate ? "Você ganha" : "Afiliado ganha"} · {affiliation.effectiveCommissionPercent.toFixed(0)}%
                    </p>
                    <p className={affiliateHighlighted ? highlightValue : neutralValue}>
                      {formatBRL((affiliation.productPrice * affiliation.effectiveCommissionPercent) / 100)}
                    </p>
                  </div>
                  {/* Recebimento do produtor — highlight se o viewer NÃO é o afiliado */}
                  <div className={producerHighlighted ? highlightCard : neutralCard}>
                    <p className={producerHighlighted ? highlightLabel : neutralLabel}>
                      {viewerIsAffiliate ? "Produtor recebe" : "Você recebe"} · {(100 - affiliation.effectiveCommissionPercent).toFixed(0)}%
                    </p>
                    <p className={producerHighlighted ? highlightValue : neutralValue}>
                      {formatBRL(affiliation.productPrice * (1 - affiliation.effectiveCommissionPercent / 100))}
                    </p>
                  </div>
                </div>
              );
            })()}
            {/* Disclaimer minúsculo: valores brutos. As taxas da plataforma
                (Stripe/OpenPix + fee Fellow Pay) saem do lado do produtor,
                não do afiliado. */}
            {affiliation.productPrice != null && (
              <p className="text-[10px] text-gray-500 dark:text-gray-400 mt-1.5">
                Valores brutos · taxas da plataforma são descontadas do produtor
              </p>
            )}
          </div>
        </div>
      </div>

      {/* Link + QR code lado a lado quando APPROVED. QR é game-changer pra
          afiliado compartilhar via Instagram bio / story (não precisa typar URL).
          QR via api.qrserver.com — sem dep adicional. Skip todo o bloco em
          status != APPROVED (sem link válido). */}
      {isApproved && affiliation.checkoutUrl && (
        <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] sm:items-center gap-4 rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 p-5">
          {/* QR code em frame estilo polaroid — bg gray-100 + padding
              desigual (laterais/topo iguais, embaixo maior para caber a
              caption). White inner area preserva contraste pro scan
              funcionar. Shadow soft para flutuação. data= é a URL absoluta
              porque o QR vai ser lido de fora. */}
          <div className="bg-gray-100 dark:bg-gray-800 p-2.5 pb-3.5 rounded-md shadow-md shadow-gray-300/40 dark:shadow-black/40">
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img
              src={`https://api.qrserver.com/v1/create-qr-code/?size=130x130&margin=8&data=${encodeURIComponent(fullCheckoutUrl)}`}
              alt="QR Code do link"
              width={130}
              height={130}
              className="block bg-white"
            />
            <p className="text-[10px] text-gray-600 dark:text-gray-300 text-center mt-2.5 font-semibold tracking-[0.1em] uppercase">
              QR para bio / story
            </p>
          </div>
          <div className="min-w-0">
            <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400 mb-2">
              {viewerIsAffiliate
                ? "Seu link de divulgação"
                : `Link do afiliado · ${affiliation.affiliateSellerName ?? "Afiliado"}`}
            </p>
            <div className="flex items-center gap-2">
              <code className="flex-1 rounded-lg bg-gray-50 dark:bg-gray-800 px-3 py-2 text-[11px] font-mono break-all text-gray-700 dark:text-gray-300">
                {fullCheckoutUrl}
              </code>
              <button
                onClick={copyLink}
                className="h-9 shrink-0 rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-xs font-medium text-white"
              >
                {copied ? "Copiado!" : "Copiar"}
              </button>
            </div>
            <p className="text-[11px] text-gray-500 dark:text-gray-400 mt-2">
              {viewerIsAffiliate ? (
                <>Compartilhe nas suas redes. Cada venda atribuída paga sua comissão automaticamente.</>
              ) : (
                <>Link único que <strong>{affiliation.affiliateSellerName ?? "—"}</strong> usa para divulgar. Tracking <code className="font-mono">{affiliation.trackingCode}</code>.</>
              )}
            </p>
          </div>
        </div>
      )}

      {/* Toolbar com seletor de período — afeta todas as métricas/funnel/spark
          abaixo. Cache é separado por valor (queryKey inclui periodDays),
          então trocar não invalida nada — fetch novo se ainda não tem cache. */}
      <div className="flex items-center justify-between gap-3 flex-wrap">
        <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400">
          Performance · últimos {periodDays} dias
        </p>
        <PeriodPicker value={periodDays} onChange={setPeriodDays} />
      </div>

      {/* Cards de métricas — única visualização da performance, sem repetir
          info em funil + cards (era confuso). Sparkline comunica trend
          (não só ponto único); delta dá comparação concreta ("você melhorou
          ou piorou"). "Taxa de conversão" substitui o funil — sintetiza
          numa stat o que o funil mostrava visualmente: quantos % dos cliques
          viraram venda. "A receber" fica de fora dos 4 cards do funnel
          conceitual (vendas históricas vs saldo pendente são coisas
          diferentes) e vira card destacado abaixo. */}
      <section>
        <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400 mb-3">
          Métricas <span className="font-normal normal-case tracking-normal text-gray-400">vs {periodDays} dias anteriores</span>
        </p>
        <div className="grid grid-cols-2 md:grid-cols-3 xl:grid-cols-5 gap-3">
          <RichStatCard
            label="Cliques"
            value={stats ? stats.clicksInPeriod.toString() : null}
            previous={stats?.previousClicksInPeriod ?? 0}
            current={stats?.clicksInPeriod ?? 0}
            sparkData={stats?.clicksByDay}
            loading={loadingStats}
            sparkColor="#6366f1"
          />
          <RichStatCard
            label="Vendas"
            value={stats ? stats.salesInPeriod.toString() : null}
            previous={stats?.previousSalesInPeriod ?? 0}
            current={stats?.salesInPeriod ?? 0}
            sparkData={stats?.salesByDay}
            loading={loadingStats}
            accent={!!stats && stats.salesInPeriod > 0}
            sparkColor="#16a34a"
          />
          <RichStatCard
            label={viewerIsAffiliate ? "Faturamento gerado" : "Faturamento via afiliado"}
            value={stats ? formatBRL(stats.tpvInPeriod) : null}
            previous={Number(stats?.previousTpvInPeriod ?? 0)}
            current={Number(stats?.tpvInPeriod ?? 0)}
            loading={loadingStats}
            accent={!!stats && stats.tpvInPeriod > 0}
          />
          <RichStatCard
            label="Taxa de conversão"
            value={stats
              ? `${(stats.conversionPercentInPeriod ?? 0).toFixed(1)}%`
              : null}
            sub={stats
              ? `${stats.salesInPeriod} venda${stats.salesInPeriod === 1 ? "" : "s"} em ${stats.clicksInPeriod} clique${stats.clicksInPeriod === 1 ? "" : "s"}`
              : undefined}
            loading={loadingStats}
            accent={!!stats && (stats.conversionPercentInPeriod ?? 0) > 0}
          />
          <RichStatCard
            label={viewerIsAffiliate ? "Sua comissão" : "Comissão paga ao afiliado"}
            value={stats ? formatBRL(stats.earningsInPeriod) : null}
            previous={Number(stats?.previousEarningsInPeriod ?? 0)}
            current={Number(stats?.earningsInPeriod ?? 0)}
            loading={loadingStats}
            accent={!!stats && stats.earningsInPeriod > 0}
          />
        </div>
      </section>

      {/* "A receber" isolado abaixo dos cards — saldo pendente é conceito
          DIFERENTE de performance histórica. Mistura na mesma row gerava
          confusão. Sticker dedicado com label clara. */}
      {stats && stats.earningsPending > 0 && (
        <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 p-5 flex items-center justify-between gap-3">
          <div>
            <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400 mb-1">
              {viewerIsAffiliate ? "Você vai receber" : "Afiliado vai receber"}
            </p>
            <p className="text-2xl font-bold text-gray-900 dark:text-white tabular-nums leading-none">
              {formatBRL(stats.earningsPending)}
            </p>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1.5">
              {viewerIsAffiliate
                ? "Aguardando liquidação na sua carteira"
                : "Aguardando liquidação na carteira do afiliado"}
            </p>
          </div>
        </div>
      )}

      {/* "Próximas ações" — empty state inteligente. Quando sem atividade,
          orienta o afiliado: copiar link, compartilhar, etc. Quando já tem
          vendas, mostra "como melhorar". Não some — sempre rica. */}
      <section>
        <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400 mb-3">
          Próximas ações
        </p>
        <NextActionsCard
          stats={stats}
          hasActivity={hasActivity}
          fullCheckoutUrl={fullCheckoutUrl}
          productSlug={affiliation.productSlug ?? ""}
          viewerIsAffiliate={viewerIsAffiliate}
        />
      </section>

      {/* Acumulado — toggle compacto, info terciária. Movido pro final pq é o
          que o usuário olha menos no dia a dia. */}
      {stats && (stats.salesAllTime > 0 || stats.clicksAllTime > 0) && (
        <section>
          <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400 mb-3">
            Acumulado (desde {affiliation.approvedAt
              ? new Date(affiliation.approvedAt).toLocaleDateString("pt-BR")
              : "início"})
          </p>
          <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 p-5">
            <div className="grid grid-cols-2 lg:grid-cols-4 gap-6">
              <InlineMetric label="Cliques" value={stats.clicksAllTime.toString()} />
              <InlineMetric label="Vendas" value={stats.salesAllTime.toString()} />
              <InlineMetric label={viewerIsAffiliate ? "Faturamento gerado" : "Faturamento via afiliado"} value={formatBRLCompact(stats.tpvAllTime)} />
              <InlineMetric label={viewerIsAffiliate ? "Sua comissão" : "Comissão do afiliado"} value={formatBRLCompact(stats.earningsAllTime)} accent />
            </div>
          </div>
        </section>
      )}

      {statsError && (
        <div className="rounded-lg bg-error-50 dark:bg-error-500/10 px-4 py-3 text-xs text-error-700 dark:text-error-400">
          Não foi possível carregar as métricas. Tente atualizar a página.
        </div>
      )}

      {/* Danger zone — só para afiliação ATIVA (APPROVED). Revogar termina
          a afiliação: o link de checkout deixa de atribuir comissão. Ambos
          os papéis (afiliado e produtor) podem chamar — backend autoriza
          baseado em quem é o requester. Label e copy mudam por viewer. */}
      {isApproved && (
        <section className="rounded-2xl border border-error-200 dark:border-error-500/30 bg-error-50/30 dark:bg-error-500/5 p-5">
          <div className="flex items-start justify-between gap-4 flex-wrap">
            <div className="min-w-0 flex-1">
              <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-error-700 dark:text-error-400 mb-1">
                Encerrar afiliação
              </p>
              <p className="text-sm text-gray-700 dark:text-gray-300">
                {viewerIsAffiliate
                  ? "Ao encerrar, seu link de divulgação deixa de atribuir comissão. Vendas em andamento até esse momento continuam contando."
                  : "Ao revogar, o link do afiliado deixa de converter. Vendas em andamento até esse momento continuam contando — comissões já creditadas não são afetadas."}
              </p>
            </div>
            <button
              type="button"
              onClick={() => setConfirmingRevoke(true)}
              disabled={revoke.isPending}
              className="h-9 rounded-lg bg-error-500 hover:bg-error-600 px-4 text-sm font-semibold text-white disabled:opacity-50 disabled:cursor-not-allowed transition-colors shrink-0"
            >
              {viewerIsAffiliate ? "Encerrar afiliação" : "Revogar afiliação"}
            </button>
          </div>
        </section>
      )}

      <ConfirmModal
        isOpen={confirmingRevoke}
        title={viewerIsAffiliate ? "Encerrar esta afiliação?" : "Revogar esta afiliação?"}
        message={
          viewerIsAffiliate
            ? "Seu link de divulgação deixa de atribuir comissão imediatamente. Essa ação é irreversível — para promover de novo você teria que solicitar uma nova afiliação."
            : "O link do afiliado deixa de converter imediatamente. Essa ação é irreversível — para reativar, o afiliado teria que solicitar uma nova afiliação."
        }
        confirmLabel={viewerIsAffiliate ? "Encerrar" : "Revogar"}
        variant="danger"
        requireCode
        isLoading={revoke.isPending}
        onConfirm={() => revoke.mutate()}
        onCancel={() => setConfirmingRevoke(false)}
      />

      {revoke.isError && (
        <div className="rounded-lg bg-error-50 dark:bg-error-500/10 px-4 py-3 text-xs text-error-700 dark:text-error-400">
          Não foi possível revogar. Tente novamente.
        </div>
      )}
    </div>
  );
}

function RichStatCard({
  label,
  value,
  loading,
  previous,
  current,
  sparkData,
  sub,
  accent,
  sparkColor,
}: {
  label: string;
  value: string | null;
  loading?: boolean;
  previous?: number;
  current?: number;
  sparkData?: number[];
  sub?: string;
  accent?: boolean;
  sparkColor?: string;
}) {
  // Delta percentage. Tratamentos:
  // - previous indefinido → não mostra delta (ex: A receber)
  // - previous == 0 e current == 0 → 0% (neutro)
  // - previous == 0 e current > 0 → "novo" (infinito)
  // - previous > 0 → variação normal
  let delta: number | "new" | null = null;
  if (previous !== undefined && current !== undefined) {
    if (previous === 0 && current === 0) delta = 0;
    else if (previous === 0) delta = "new";
    else delta = ((current - previous) / previous) * 100;
  }

  return (
    <div className="rounded-2xl border border-gray-200/80 bg-white p-5 dark:border-gray-800 dark:bg-gray-900 flex flex-col gap-2">
      <div className="flex items-center justify-between">
        <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400">
          {label}
        </p>
        {delta !== null && !loading && value !== null && (
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
      {loading || value === null ? (
        <div className="h-7 w-20 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />
      ) : (
        <p className={`text-[22px] leading-[1.15] font-semibold tabular-nums tracking-tight whitespace-nowrap ${
          accent ? "text-success-600 dark:text-success-400" : "text-gray-900 dark:text-white"
        }`}>
          {value}
        </p>
      )}
      {sub && (
        <p className="text-[11px] text-gray-500 dark:text-gray-400">{sub}</p>
      )}
      {sparkData && sparkData.length > 1 && (
        <div className="-mx-2 -mb-2 mt-1">
          <Sparkline data={sparkData} color={sparkColor ?? "#b026ff"} height={36} ariaLabel={`${label} nos últimos 30 dias`} />
        </div>
      )}
    </div>
  );
}

function InlineMetric({ label, value, accent }: { label: string; value: string; accent?: boolean }) {
  // Tipografia espelha a do RichStatCard para consistência: text-[22px] +
  // leading-[1.15] + font-semibold + tracking-tight. Label igualmente
  // (text-[10px] font-semibold tracking-[0.06em]).
  return (
    <div>
      <p className="text-[10px] font-semibold uppercase tracking-[0.06em] text-gray-500 dark:text-gray-400 mb-1">
        {label}
      </p>
      <p className={`text-[22px] leading-[1.15] font-semibold tabular-nums tracking-tight whitespace-nowrap ${
        accent ? "text-success-600 dark:text-success-400" : "text-gray-900 dark:text-white"
      }`}>
        {value}
      </p>
    </div>
  );
}

/**
 * Card de orientação contextual. Renderiza 2-3 ações úteis baseado no estado
 * atual da afiliação: sem cliques ainda → "compartilhe o link". Cliques mas
 * sem venda → "mostre prova social, materiais do produto". Vendendo → "veja
 * leaderboard, top performers do produto".
 */
function NextActionsCard({
  stats,
  hasActivity,
  fullCheckoutUrl,
  productSlug,
  viewerIsAffiliate,
}: {
  stats: import("@/services/marketplace.service").AffiliateStats | undefined;
  hasActivity: boolean;
  fullCheckoutUrl: string;
  productSlug: string;
  viewerIsAffiliate: boolean;
}) {
  // Cenários priorizados — mostra o mais relevante baseado no estado.
  const actions: { icon: string; title: string; desc: string; href?: string; cta?: string }[] = [];

  if (!hasActivity) {
    actions.push({
      icon: "📣",
      title: viewerIsAffiliate ? "Compartilhe seu link" : "Link pronto para divulgação",
      desc: viewerIsAffiliate
        ? "Cole o link no Instagram bio, story, WhatsApp ou anúncio pago. Cada visita conta como clique."
        : "Esse afiliado ainda não fez cliques. Confirme que ele tem o link e está divulgando.",
    });
    if (productSlug) {
      actions.push({
        icon: "🔗",
        title: "Veja o checkout",
        desc: "Abra a página pública para entender a experiência do cliente — mesma URL que ele vai ver.",
        href: fullCheckoutUrl || `/p/${productSlug}`,
        cta: "Abrir checkout",
      });
    }
  } else if (stats && stats.clicksInPeriod > 0 && stats.salesInPeriod === 0) {
    actions.push({
      icon: "🎯",
      title: "Cliques sem conversão",
      desc: `Você teve ${stats.clicksInPeriod} cliques mas nenhuma venda. Considere adicionar prova social ou explicar melhor o benefício do produto.`,
    });
    if (productSlug) {
      actions.push({
        icon: "👁",
        title: "Revise a página de checkout",
        desc: "Veja como o cliente final encontra o produto. Talvez o copy ou preço esteja causando bounce.",
        href: fullCheckoutUrl || `/p/${productSlug}`,
        cta: "Abrir checkout",
      });
    }
  } else if (stats && stats.salesInPeriod > 0) {
    actions.push({
      icon: "🚀",
      title: "Você está vendendo!",
      desc: `${stats.salesInPeriod} ${stats.salesInPeriod === 1 ? "venda" : "vendas"} no período. Replique o que tá funcionando: mesmos canais, mesmo formato, mesma frequência.`,
    });
    actions.push({
      icon: "📈",
      title: "Escale o canal vencedor",
      desc: "Identifique qual rede/anúncio trouxe mais cliques convertidos e dobre o esforço lá.",
    });
  }

  return (
    <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 p-5">
      <div className="space-y-3">
        {actions.map((a, idx) => (
          <div key={idx} className="flex items-start gap-3">
            <div className="text-xl shrink-0 leading-none mt-0.5" aria-hidden="true">{a.icon}</div>
            <div className="flex-1 min-w-0">
              <p className="text-sm font-medium text-gray-900 dark:text-white">{a.title}</p>
              <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">{a.desc}</p>
            </div>
            {a.href && a.cta && (
              <a
                href={a.href}
                target="_blank"
                rel="noopener noreferrer"
                className="shrink-0 h-8 inline-flex items-center rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-800 px-3 text-xs font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-700"
              >
                {a.cta}
              </a>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

