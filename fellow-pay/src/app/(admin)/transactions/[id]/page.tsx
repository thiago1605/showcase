"use client";
import React, { useState, useEffect } from "react";
import { useScrollLock } from "@/hooks/useScrollLock";
import Link from "next/link";
import { useParams } from "next/navigation";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { transactionsService, type RefundBreakdown } from "@/services/transactions.service";
import Input from "@/components/form/input/InputField";
import { PaymentMethodBadge } from "@/components/ui/PaymentMethodBadge";
import { DetailPageSkeleton } from "@/components/ui/Skeleton";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { BackLink } from "@/components/ui/BackLink";
import { transactionStatusKey, transactionStatusLabel, paymentTypeLabel } from "@/lib/formatters/enums";
import { Tooltip } from "@/components/ui/Tooltip";
import { ApiError } from "@/lib/api/client";
import { useSellerTier } from "@/hooks/useSellerTier";
import type { SellerTierCode, Transaction } from "@/types";

// Mapping label do enum no backend pro nome visual. Identity após Sprint 2
// (PLATINUM foi renomeado pra DIAMOND no backend; antes era um mapping
// PLATINUM→"Diamond"). Mantido como Record pra robustez de tipo + facilidade
// de localização futura. Mesmo mapping usado em TierBadge / TierCard /
// TierPremiumCard pra consistência.
const TIER_LABEL: Record<SellerTierCode, string> = {
  SILVER: "Silver",
  GOLD: "Gold",
  DIAMOND: "Diamond",
  BLACK: "Black",
  INFINITE: "Infinite",
};

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

/**
 * Formato curto sem segundos pro detalhe de transação. Segundos no painel do
 * seller são ruído — granularidade de minuto basta. A versão com segundos
 * fica reservada pra logs de auditoria interna (se necessário).
 */
function formatDate(dateStr: string) {
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" }).format(new Date(dateStr));
}

export default function TransactionDetailPage() {
  const params = useParams();
  const id = params.id as string;
  // Hook do tier do seller pra anotar o tooltip da taxa com o nível atual.
  // Sprint 1.5 derrubou PricingPlan — o tier (Silver/Gold/Diamond/Black/Infinite)
  // é a fonte da taxa cobrada. Mostrar no tooltip reforça o sistema de níveis
  // pro seller a cada transação. Falha 403 (operadores da plataforma) deixa
  // tier null e a linha some — sem ruído pra usuários sem tier.
  const { tier: sellerTier } = useSellerTier();
  const [transaction, setTransaction] = useState<Transaction | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState("");
  const [showRefundModal, setShowRefundModal] = useState(false);
  const [refundAmount, setRefundAmount] = useState("");
  /**
   * Motivo do reembolso — anotação LIVRE do seller. Armazenado em
   * `RefundIntent.Reason` no banco pra histórico/auditoria. NÃO vai direto
   * pro Stripe — o `StripePaymentProvider.SanitizeStripeReason` traduz pra
   * um dos 3 enums aceitos antes da chamada, então texto livre não quebra
   * mais o refund (era o bug do "Invalid reason").
   */
  const [refundReason, setRefundReason] = useState("");
  const [refundLoading, setRefundLoading] = useState(false);
  const [refundSuccess, setRefundSuccess] = useState(false);
  /** Erro restrito ao modal — não polui o resto da página. */
  const [refundError, setRefundError] = useState<string | null>(null);
  /**
   * Breakdown do reembolso, atualizado conforme o seller digita o valor
   * (debounced). Quando preenchido, mostra a quebra detalhada no modal
   * pra evitar surpresa pós-confirmação ("por que descontaram mais do que
   * eu reembolsei?"). Null = ainda não temos dados pra mostrar.
   */
  const [refundPreview, setRefundPreview] = useState<RefundBreakdown | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  /**
   * Erro de validação INLINE do campo de valor (separado do refundError, que
   * é erro de submit). Bloqueia o preview e o submit quando o valor está
   * fora do range — sem isso, o input HTML `max` só barra setinhas, deixa
   * digitar qualquer coisa.
   */
  const [refundAmountError, setRefundAmountError] = useState<string | null>(null);

  // Trava scroll enquanto o modal de reembolso estiver aberto.
  useScrollLock(showRefundModal);

  useEffect(() => {
    async function load() {
      try {
        const data = await transactionsService.getById(id);
        setTransaction(data);
      } catch {
        setError("Transação não encontrada.");
      }
      setIsLoading(false);
    }
    load();
  }, [id]);

  /**
   * Debounced refund preview. Recalcula a quebra a cada 300ms após o seller
   * parar de digitar — evita storm de requests, mantém a UI responsiva, e
   * mostra os números reais (taxa que vai pagar, etc.) antes do confirmar.
   *
   * Roda só quando o modal está aberto e o valor é parseável + > 0.
   * Erros no preview (valor inválido, etc.) silenciosamente limpam o breakdown
   * — o erro real aparece na hora do submit, sem assustar antes da hora.
   */
  useEffect(() => {
    if (!showRefundModal || !transaction) {
      setRefundPreview(null);
      setRefundAmountError(null);
      return;
    }
    const amount = parseFloat(refundAmount);
    if (refundAmount === "" || Number.isNaN(amount)) {
      // Estado neutro: campo vazio ou input inválido — não mostra preview nem erro.
      setRefundPreview(null);
      setRefundAmountError(null);
      return;
    }
    if (amount <= 0) {
      setRefundPreview(null);
      setRefundAmountError("Informe um valor maior que zero.");
      return;
    }
    const max = transaction.amount - transaction.refundedAmount;
    if (amount > max + 0.001) {
      // Bloqueia o preview ANTES de bater no backend — sem isso, o seller via
      // breakdown calculado pra valor inválido (bug do print do usuário).
      setRefundPreview(null);
      setRefundAmountError(`Valor máximo permitido: ${formatCurrency(max)}.`);
      return;
    }
    // Valor válido — limpa erro e busca preview no backend
    setRefundAmountError(null);
    setPreviewLoading(true);
    const timer = setTimeout(async () => {
      try {
        const preview = await transactionsService.previewRefund(transaction.id, amount);
        setRefundPreview(preview);
      } catch {
        setRefundPreview(null);
      } finally {
        setPreviewLoading(false);
      }
    }, 300);
    return () => clearTimeout(timer);
  }, [refundAmount, showRefundModal, transaction]);

  const handleRefund = async (e: React.FormEvent) => {
    e.preventDefault();
    setRefundError(null);

    // Validação client-side — evita mandar request inválido pro backend e dá
    // feedback imediato. Valores fora do range causariam erros genéricos que
    // são UX ruim. parseFloat("") = NaN, parseFloat("0") = 0; ambos inválidos.
    const amount = parseFloat(refundAmount);
    if (!transaction) return;
    if (Number.isNaN(amount) || amount <= 0) {
      setRefundError("Informe um valor maior que zero.");
      return;
    }
    const max = transaction.amount - transaction.refundedAmount;
    if (amount > max + 0.001) {
      setRefundError(`Valor máximo permitido: R$ ${max.toFixed(2)}.`);
      return;
    }

    setRefundLoading(true);
    try {
      await transactionsService.refund(transaction.id, amount, refundReason || undefined);
      setRefundSuccess(true);
      setShowRefundModal(false);
      setRefundAmount("");
      setRefundReason("");
      setRefundPreview(null);
      setRefundAmountError(null);
      // Reload transaction
      const updated = await transactionsService.getById(transaction.id);
      setTransaction(updated);
    } catch (err) {
      // Erros conhecidos do backend (Refund.NotAllowed, AlreadyCompleted, etc.)
      // chegam via ApiError com a mensagem do BusinessException — mostramos
      // exatamente o que o backend disse pra que o seller possa agir.
      if (err instanceof ApiError) {
        setRefundError(err.message || "Não foi possível processar o reembolso.");
      } else {
        setRefundError("Erro de conexão. Tente novamente.");
      }
    } finally {
      setRefundLoading(false);
    }
  };

  if (isLoading) {
    return <DetailPageSkeleton ariaLabel="Carregando transação" />;
  }

  if (error && !transaction) {
    return (
      <div className="space-y-4">
        <BackLink fallbackHref="/transactions" />
        <div className="p-4 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">
          {error}
        </div>
      </div>
    );
  }

  if (!transaction) return null;

  // Backend serializa status como int (System.Text.Json default) — resolvemos
  // pra key string antes de comparar com a lista de status reembolsáveis.
  const statusKey = transactionStatusKey(transaction.status);
  const canRefund = !!statusKey && ["CAPTURED", "AUTHORIZED"].includes(statusKey) && transaction.refundedAmount < transaction.amount;
  const maxRefund = transaction.amount - transaction.refundedAmount;

  // Taxa REAL paga pelo seller = platformFeeAmount (vem do PricingPlan moderno).
  // O `feeAmount` legado fica 0 quando o seller usa plano (Bruce Wayne case).
  // Fallback pra feeAmount caso ainda haja transações antigas sem o campo novo.
  const fee = transaction.platformFeeAmount ?? transaction.feeAmount ?? 0;
  const netDisplay = transaction.amount - fee;
  const effectiveRate = transaction.amount > 0 ? (fee / transaction.amount) * 100 : 0;
  const methodLabel = paymentTypeLabel(transaction.paymentType);

  // Breakdown da taxa (rate × amount + fixed) — vindo do backend baseado no
  // plano vigente do seller. Pode ser null se o seller não tem plano ou se o
  // método não tem percentual definido.
  const ratePercent = transaction.platformFeeRatePercent;
  const fixedFee = transaction.platformFeeFixedAmount;
  const hasBreakdown = ratePercent != null && fixedFee != null;
  const percentComponent = hasBreakdown ? (ratePercent! / 100) * transaction.amount : 0;

  const hasPayerData = !!(transaction.payerName || transaction.payerEmail || transaction.payerDocument);

  // Nível vigente do seller (Sprint 1.5+ — fonte da taxa, substitui PricingPlan).
  // Pra transações antigas pré-Sprint 1.5 esta é a taxa do nível ATUAL, não a do
  // momento histórico — aceitável como aproximação até o backend persistir
  // snapshot do tier no momento da captura.
  const tierLabel = sellerTier ? TIER_LABEL[sellerTier.currentTier] : null;

  return (
    <div className="space-y-6">
      <BackLink fallbackHref="/transactions" />

      {/* Breadcrumb — preserva ID display + contexto de hierarquia. BackLink
          acima é a ação ("voltar pra de onde vim"); breadcrumb é orientação
          ("onde estou na hierarquia"). */}
      <div className="flex items-center gap-2 text-sm">
        <Link href="/transactions" className="text-brand-500 hover:text-brand-600">Transações</Link>
        <span className="text-gray-400">/</span>
        <IdDisplay id={id} />
      </div>

      {/* Header — valor como hero principal da página. Antes em text-xl (20px)
          competia com títulos de card; agora text-3xl (30px) deixa claro qual
          é a informação mais importante. Status badge ganha porte visível.
          Reembolsar vira CTA primário filled (era outline pequeno) — ação
          destrutiva mas é a principal ação possível nesta tela. */}
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0 flex-1">
          <h1 className="text-3xl font-semibold text-gray-900 dark:text-white tracking-tight tabular-nums">
            {formatCurrency(transaction.amount)}
          </h1>
          {transaction.description && (
            <p className="text-sm text-gray-500 dark:text-gray-400 mt-1.5 truncate">
              {transaction.description}
            </p>
          )}
        </div>
        <div className="flex items-center gap-3 shrink-0 mt-1.5">
          <StatusBadge status={transaction.status} kind="transaction" />
          {canRefund && (
            <button
              onClick={() => {
                // Reset state ao abrir — evita herdar erro/valor da tentativa anterior.
                setRefundError(null);
                setRefundAmount("");
                setRefundReason("");
                setRefundPreview(null);
                setRefundAmountError(null);
                setShowRefundModal(true);
              }}
              className="h-9 rounded-lg bg-error-600 px-4 text-sm font-semibold text-white hover:bg-error-700 transition-colors"
            >
              Reembolsar
            </button>
          )}
        </div>
      </div>

      {refundSuccess && (
        <div className="p-3 rounded-lg bg-success-50 dark:bg-success-500/10 text-success-700 dark:text-success-400 text-sm">
          Reembolso processado com sucesso.
        </div>
      )}

      {error && (
        <div className="p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">
          {error}
        </div>
      )}

      {/* Details Grid — 3 colunas no desktop: Detalhes ocupa 2 (tem muito
          campo, precisa de respiro), Pagador + Timeline empilham na coluna
          direita (resolve a asymmetria do layout 2-cols antigo, onde Pagador
          terminava cedo e deixava vazio embaixo + Timeline órfão full-width
          abaixo). No mobile vira coluna única empilhada normalmente. */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
        {/* Transaction Info — span 2 no desktop */}
        <div className="lg:col-span-2 rounded-xl border border-gray-200 bg-white p-6 dark:border-gray-800 dark:bg-gray-900">
          <h2 className="text-base font-semibold text-gray-900 dark:text-white mb-5">Detalhes da Transação</h2>
          <dl className="space-y-3 text-sm">
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">ID</dt>
              <dd><IdDisplay id={transaction.id} copyable /></dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">Valor bruto</dt>
              <dd className="text-gray-900 dark:text-white">{formatCurrency(transaction.amount)}</dd>
            </div>
            <div className="flex justify-between items-center">
              <dt className="text-gray-500 dark:text-gray-400 inline-flex items-center gap-1">
                Taxa Fellow Pay
                <Tooltip
                  side="right"
                  maxWidth={280}
                  content={
                    <div className="space-y-2">
                      <div>
                        <p className="font-semibold text-[12px]">Composição da taxa</p>
                        <p className="opacity-80 text-[10px]">
                          {methodLabel}
                          {/* Nível do seller substitui o legado `Plano X` (PricingPlan
                              foi descontinuado na Sprint 1.5 — tier é a fonte da
                              taxa). Fallback pra pricingPlanCode mantido por
                              compatibilidade com transações antigas que ainda
                              carregam o campo legado. */}
                          {tierLabel
                            ? ` · Nível ${tierLabel}`
                            : transaction.pricingPlanCode &&
                              ` · Plano ${transaction.pricingPlanCode}`}
                        </p>
                      </div>

                      {hasBreakdown ? (
                        <div className="border-t border-white/20 pt-2 space-y-1 tabular-nums">
                          <div className="flex items-baseline justify-between gap-3">
                            <span className="opacity-90">
                              {ratePercent!.toFixed(2).replace(".", ",")}% × {formatCurrency(transaction.amount)}
                            </span>
                            <span>{formatCurrency(percentComponent)}</span>
                          </div>
                          <div className="flex items-baseline justify-between gap-3">
                            <span className="opacity-90">Tarifa fixa</span>
                            <span>{formatCurrency(fixedFee!)}</span>
                          </div>
                          <div className="flex items-baseline justify-between gap-3 pt-1 border-t border-white/20 font-semibold">
                            <span>Total</span>
                            <span>
                              {formatCurrency(fee)}{" "}
                              <span className="opacity-70 font-normal">
                                ({effectiveRate.toFixed(2).replace(".", ",")}%)
                              </span>
                            </span>
                          </div>
                        </div>
                      ) : (
                        <p className="opacity-80 text-[11px]">
                          Total: {formatCurrency(fee)} ({effectiveRate.toFixed(2).replace(".", ",")}% sobre {formatCurrency(transaction.amount)}).
                        </p>
                      )}
                    </div>
                  }
                >
                  <button
                    type="button"
                    aria-label="Sobre a taxa Fellow Pay"
                    className="text-gray-400 hover:text-gray-600 dark:text-gray-500 dark:hover:text-gray-300 transition-colors"
                  >
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                      <circle cx="12" cy="12" r="10" />
                      <path d="M12 16v-4" />
                      <path d="M12 8h.01" />
                    </svg>
                  </button>
                </Tooltip>
              </dt>
              <dd className="text-gray-900 dark:text-white tabular-nums">{formatCurrency(fee)}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">Valor líquido</dt>
              <dd className="text-gray-900 dark:text-white font-medium tabular-nums">{formatCurrency(netDisplay)}</dd>
            </div>
            {transaction.refundedAmount > 0 && (
              <div className="flex justify-between">
                <dt className="text-gray-500 dark:text-gray-400">Reembolsado</dt>
                <dd className="text-error-600 dark:text-error-400">{formatCurrency(transaction.refundedAmount)}</dd>
              </div>
            )}
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">Método</dt>
              <dd><PaymentMethodBadge type={transaction.paymentType} /></dd>
            </div>
            {transaction.installments > 1 && (
              <div className="flex justify-between">
                <dt className="text-gray-500 dark:text-gray-400">Parcelas</dt>
                <dd className="text-gray-900 dark:text-white">{transaction.installments}x</dd>
              </div>
            )}
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">Criada em</dt>
              <dd className="text-gray-900 dark:text-white">{formatDate(transaction.createdAt)}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">Atualizada em</dt>
              <dd className="text-gray-900 dark:text-white">{formatDate(transaction.updatedAt)}</dd>
            </div>
          </dl>
        </div>

        {/* Coluna direita: Pagador + Timeline empilhados. Antes Timeline ficava
            full-width abaixo do grid 2-cols, criando "L invertido" de espaço
            vazio. Agora ocupa a coluna direita junto com Pagador, fechando
            o layout sem buracos. */}
        <div className="space-y-6">
          {/* Payer Info */}
          <div className="rounded-xl border border-gray-200 bg-white p-6 dark:border-gray-800 dark:bg-gray-900">
            <h2 className="text-base font-semibold text-gray-900 dark:text-white mb-5">Dados do Pagador</h2>
            {hasPayerData ? (
              <dl className="space-y-3 text-sm">
                {transaction.payerName && (
                  <div className="flex justify-between gap-3">
                    <dt className="text-gray-500 dark:text-gray-400 shrink-0">Nome</dt>
                    <dd className="text-gray-900 dark:text-white text-right truncate">{transaction.payerName}</dd>
                  </div>
                )}
                {transaction.payerEmail && (
                  <div className="flex justify-between gap-3">
                    <dt className="text-gray-500 dark:text-gray-400 shrink-0">Email</dt>
                    <dd className="text-gray-900 dark:text-white text-right truncate">{transaction.payerEmail}</dd>
                  </div>
                )}
                {transaction.payerDocument && (
                  <div className="flex justify-between gap-3">
                    <dt className="text-gray-500 dark:text-gray-400 shrink-0">Documento</dt>
                    <dd className="text-gray-900 dark:text-white font-mono text-xs">{transaction.payerDocument}</dd>
                  </div>
                )}
              </dl>
            ) : (
              <p className="text-sm text-gray-500 dark:text-gray-400">Nenhum dado do pagador disponível.</p>
            )}
          </div>

          {/* Timeline. Itera sobre `transaction.refunds` pra exibir cada
              RefundIntent COMPLETED individualmente (alinhado com /refunds).
              FAILED é omitido pra não poluir. */}
          <div className="rounded-xl border border-gray-200 bg-white p-6 dark:border-gray-800 dark:bg-gray-900">
            <h2 className="text-base font-semibold text-gray-900 dark:text-white mb-5">Timeline</h2>
            <div className="space-y-4">
              <TimelineItem label="Transação criada" date={transaction.createdAt} active />
              {statusKey && statusKey !== "CREATED" && statusKey !== "REFUNDED" && (
                <TimelineItem
                  label={transactionStatusLabel(transaction.status)}
                  date={transaction.updatedAt}
                  active
                />
              )}
              {(transaction.refunds ?? [])
                .filter((r) => r.status === "COMPLETED")
                .map((r) => (
                  <TimelineItem
                    key={r.id}
                    label={`Reembolso de ${formatCurrency(r.amount)}`}
                    date={r.createdAt}
                    active
                  />
                ))}
              {statusKey === "REFUNDED" && (
                <TimelineItem
                  label="Reembolsada"
                  date={transaction.updatedAt}
                  active
                />
              )}
            </div>
          </div>
        </div>
      </div>

      {/* Refund Modal */}
      {showRefundModal && (
        <div className="fixed inset-0 z-[100000] flex items-center justify-center bg-gray-400/5 backdrop-blur-[3px] modal-backdrop-in">
          <div className="w-full max-w-md rounded-xl bg-white p-6 dark:bg-gray-900 shadow-xl modal-content-in">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-4">Solicitar Reembolso</h3>
            <p className="text-xs text-gray-500 dark:text-gray-400 mb-4">
              Disponível para reembolso:{" "}
              <span className="font-medium text-gray-900 dark:text-white tabular-nums">
                {formatCurrency(maxRefund)}
              </span>
            </p>
            <form onSubmit={handleRefund} className="space-y-4">
              {/* Usa o componente Input compartilhado (stacked-label) pra ficar
                  visualmente alinhado com o resto do app — mesma altura, mesma
                  borda, mesma tipografia. */}
              <Input
                label="Valor (R$)"
                type="number"
                step={0.01}
                min="0.01"
                max={String(maxRefund)}
                value={refundAmount}
                onChange={(e) => setRefundAmount(e.target.value)}
                placeholder={maxRefund.toFixed(2).replace(".", ",")}
                required
                autoFocus
                error={refundAmountError !== null}
                hint={refundAmountError ?? undefined}
              />
              {/* Texto LIVRE — anotação interna do seller que fica gravada
                  no RefundIntent.Reason. O backend (StripePaymentProvider)
                  traduz pra um dos enums aceitos pelo Stripe antes da
                  chamada, então qualquer texto aqui é seguro. */}
              <Input
                label="Motivo (opcional)"
                type="text"
                value={refundReason}
                onChange={(e) => setRefundReason(e.target.value)}
                maxLength={200}
                placeholder="Ex: produto devolvido, cliente arrependido…"
              />

              {/* Quebra do reembolso — POLÍTICA GROSS INTEGRAL.
                  Cliente recebe = Débito da carteira (= mesmo valor sempre).
                  Decomposição interna (net + taxa) foi removida porque
                  confundia: "Taxa não estornável" era lida como perda nova
                  da plataforma quando na verdade era a taxa que o seller já
                  tinha pago na captura. Mantemos só o que importa pro seller
                  no momento do click + uma nota informativa abaixo. */}
              {refundPreview && (
                /* Background/border/padding alinhados com o componente Input
                   pra UI ficar coesa: mesmo bg-gray-50 / bg-gray-900/60, mesma
                   border-gray-200/80, mesmo px-4. Tipografia text-[14px] pra
                   bater com o tamanho do texto dos inputs. */
                <div className="rounded-lg border border-gray-200/80 dark:border-gray-800 bg-gray-50 dark:bg-gray-900/60 px-4 py-3.5 space-y-2.5">
                  <div className="flex justify-between text-[13px] text-gray-600 dark:text-gray-400">
                    <span>Cliente recebe</span>
                    <span className="tabular-nums">{formatCurrency(refundPreview.customerRefund)}</span>
                  </div>
                  <div className="flex justify-between text-[14px] font-semibold text-gray-900 dark:text-white">
                    <span>Débito da sua carteira</span>
                    <span className="tabular-nums">{formatCurrency(refundPreview.sellerTotalDebit)}</span>
                  </div>
                  {refundPreview.platformFeeWithheld > 0 && (
                    <p className="text-[11px] text-gray-500 dark:text-gray-400 pt-2 border-t border-gray-200/80 dark:border-gray-800">
                      Você devolve o valor integral. A taxa de{" "}
                      <span className="tabular-nums font-medium">{formatCurrency(refundPreview.platformFeeWithheld)}</span>{" "}
                      paga na captura não é estornada.
                    </p>
                  )}
                </div>
              )}
              {previewLoading && !refundPreview && (
                <div className="rounded-lg border border-gray-200/80 dark:border-gray-800 bg-gray-50 dark:bg-gray-900/60 px-4 py-3.5 text-[13px] text-gray-400 dark:text-gray-500">
                  Calculando quebra do reembolso…
                </div>
              )}

              {/* Erro do reembolso fica DENTRO do modal — o seller vê a mensagem
                  específica do backend (Refund.NotAllowed, AlreadyCompleted, etc.) */}
              {refundError && (
                <div
                  role="alert"
                  className="rounded-md bg-error-50 dark:bg-error-500/10 px-3 py-2 text-sm text-error-700 dark:text-error-400"
                >
                  {refundError}
                </div>
              )}

              {/* Botões com peso visual equilibrado: Cancelar ganha borda
                  pra não ficar "fraco" do lado do Confirmar sólido. Altura
                  (h-11) e radius alinhados com Input bare. */}
              <div className="flex justify-end gap-2 pt-1">
                <button
                  type="button"
                  onClick={() => setShowRefundModal(false)}
                  disabled={refundLoading}
                  className="h-11 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-50 transition-colors"
                >
                  Cancelar
                </button>
                {/* Confirmar só habilita quando o breakdown chegou do backend.
                    Sem isso o seller podia clicar antes de ver "o que vai sair
                    da carteira", contornando o objetivo da quebra. Também
                    bloqueia durante previewLoading (calculando) — assim a
                    transição não pisca pra "habilitado" entre digitação e
                    response do preview. */}
                <button
                  type="submit"
                  disabled={
                    refundLoading ||
                    refundAmountError !== null ||
                    refundAmount === "" ||
                    refundPreview === null ||
                    previewLoading
                  }
                  className="h-11 rounded-lg bg-error-600 px-4 text-sm font-semibold text-white hover:bg-error-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  {refundLoading ? "Processando..." : "Confirmar Reembolso"}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}

function TimelineItem({ label, date, active }: { label: string; date: string; active?: boolean }) {
  return (
    <div className="flex items-start gap-3">
      <div className={`mt-1 w-2.5 h-2.5 rounded-full ${active ? "bg-brand-500" : "bg-gray-300 dark:bg-gray-600"}`} />
      <div>
        <p className="text-sm text-gray-900 dark:text-white">{label}</p>
        <p className="text-xs text-gray-500 dark:text-gray-400">
          {new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" }).format(new Date(date))}
        </p>
      </div>
    </div>
  );
}
