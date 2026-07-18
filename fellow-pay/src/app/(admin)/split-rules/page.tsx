"use client";
import React, { useEffect, useState } from "react";
import Link from "next/link";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { splitRulesService } from "@/services/split-rules.service";
import { CardListSkeleton } from "@/components/ui/Skeleton";
import { ConfirmModal } from "@/components/ui/ConfirmModal";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { getCurrentSellerId } from "@/context/AuthContext";
import { PageHeader, PageHeaderButton } from "@/components/ui/PageHeader";
import type { SplitRule, SplitRuleRecipient } from "@/types";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(iso: string) {
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "short", year: "numeric" })
    .format(new Date(iso));
}

// Hash determinístico do ID → cor de avatar. Mantém recipient sempre com a mesma
// cor entre renders, sem precisar guardar nada.
const AVATAR_PALETTE = [
  "bg-brand-100 text-brand-700 dark:bg-brand-500/20 dark:text-brand-300",
  "bg-success-100 text-success-700 dark:bg-success-500/20 dark:text-success-300",
  "bg-blue-light-100 text-blue-light-700 dark:bg-blue-light-500/20 dark:text-blue-light-300",
  "bg-warning-100 text-warning-700 dark:bg-warning-500/20 dark:text-warning-300",
  "bg-orange-100 text-orange-700 dark:bg-orange-500/20 dark:text-orange-300",
];
function colorFor(id: string): string {
  let hash = 0;
  for (let i = 0; i < id.length; i++) hash = (hash * 31 + id.charCodeAt(i)) | 0;
  return AVATAR_PALETTE[Math.abs(hash) % AVATAR_PALETTE.length];
}

interface RecipientRowProps {
  recipient: SplitRuleRecipient;
  mySellerId: string | null;
}

function RecipientRow({ recipient, mySellerId }: RecipientRowProps) {
  const isMe = recipient.sellerId === mySellerId;
  const displayName = isMe ? "Você" : (recipient.sellerName ?? "Seller parceiro");
  const initial = displayName.charAt(0).toUpperCase();
  const colorClass = isMe
    ? "bg-brand-500 text-white"
    : colorFor(recipient.sellerId);

  return (
    <div className="flex items-start gap-3 py-3 border-b border-gray-100 last:border-0 dark:border-gray-800">
      <span className={`shrink-0 inline-flex items-center justify-center w-8 h-8 rounded-full text-xs font-semibold ${colorClass}`}>
        {initial}
      </span>
      <div className="flex-1 min-w-0">
        <p className="text-sm font-medium text-gray-900 dark:text-white truncate" title={displayName}>
          {displayName}
        </p>
        <IdDisplay id={recipient.sellerId} mineId={isMe ? mySellerId : null} mineLabel="" copyable />
      </div>
      <div className="shrink-0 text-right space-y-0.5">
        <div className="flex items-baseline justify-end gap-1">
          {recipient.percentage > 0 && (
            <span className="text-base font-semibold text-gray-900 dark:text-white tabular-nums">
              {recipient.percentage}%
            </span>
          )}
          {recipient.fixedAmount > 0 && (
            <span className="text-sm text-gray-700 dark:text-gray-300 tabular-nums">
              {recipient.percentage > 0 ? "+ " : ""}{formatCurrency(recipient.fixedAmount)}
            </span>
          )}
        </div>
        <p className="text-[11px] text-gray-500 dark:text-gray-400">prioridade {recipient.priority}</p>
      </div>
    </div>
  );
}

export default function SplitRulesPage() {
  const queryClient = useQueryClient();
  const [actionError, setActionError] = useState<string | null>(null);
  const [mySellerId, setMySellerId] = useState<string | null>(null);
  const [pending, setPending] = useState<{ id: string; name: string } | null>(null);
  const [pendingRunning, setPendingRunning] = useState(false);
  // Recipients ficam colapsados por default — row vira flat list compacta,
  // recipiente expande sob demanda. Mais alinhado com /transactions e outras
  // listings flat do app.
  const [expandedRuleIds, setExpandedRuleIds] = useState<Set<string>>(new Set());
  const toggleExpanded = (id: string) =>
    setExpandedRuleIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const { data: rawRules = [], isLoading, error: queryError } = useQuery<SplitRule[]>({
    queryKey: ["split-rules", "list"],
    queryFn: () => splitRulesService.list(),
  });
  // Ativas primeiro; tiebreaker mantém a ordem do backend (createdAt desc).
  const rules = React.useMemo(
    () => rawRules.slice().sort((a, b) => Number(b.isActive) - Number(a.isActive)),
    [rawRules],
  );
  const loadError = queryError instanceof Error ? queryError.message : queryError ? "Não foi possível carregar as regras de split." : null;
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["split-rules"] });

  useEffect(() => {
    setMySellerId(getCurrentSellerId());
  }, []);

  const handleActivate = async (id: string) => {
    setActionError(null);
    try {
      await splitRulesService.activate(id);
      invalidate();
    } catch (err) {
      setActionError(err instanceof Error ? err.message : "Erro ao ativar a regra.");
    }
  };

  const askDeactivate = (id: string, name: string) => {
    setPending({ id, name });
  };

  const runDeactivate = async () => {
    if (!pending) return;
    setActionError(null);
    setPendingRunning(true);
    try {
      await splitRulesService.deactivate(pending.id);
      invalidate();
      setPending(null);
    } catch (err) {
      setActionError(err instanceof Error ? err.message : "Erro ao desativar a regra.");
    }
    setPendingRunning(false);
  };

  if (isLoading) {
    return <CardListSkeleton count={3} ariaLabel="Carregando regras de split" />;
  }

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Regras de Split"
        subtitle="Regras criadas por você e regras onde você é destinatário. Novas regras nascem como rascunho — ative antes de usar em um payment link."
        actions={<PageHeaderButton href="/split-rules/new">+ Nova regra</PageHeaderButton>}
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <line x1="6" y1="3" x2="6" y2="15" />
            <circle cx="18" cy="6" r="3" />
            <circle cx="6" cy="18" r="3" />
            <path d="M18 9a9 9 0 0 1-9 9" />
          </svg>
        }
      />

      {loadError && (
        <div className="rounded-lg border border-error-200 bg-error-50 px-4 py-3 text-sm text-error-700 dark:border-error-500/30 dark:bg-error-500/10 dark:text-error-300">
          {loadError}
        </div>
      )}
      {actionError && (
        <div className="rounded-lg border border-error-200 bg-error-50 px-4 py-3 text-sm text-error-700 dark:border-error-500/30 dark:bg-error-500/10 dark:text-error-300">
          {actionError}
        </div>
      )}

      {/* Toolbar com contador — padrão dos listings do app. */}
      {rules.length > 0 && (
        <p className="text-xs text-gray-500 dark:text-gray-400 tabular-nums">
          {rules.length} {rules.length === 1 ? "regra" : "regras"}
        </p>
      )}

      {rules.length === 0 ? (
        <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] p-10 text-center">
          <div className="inline-flex items-center justify-center w-12 h-12 rounded-full bg-gray-100 dark:bg-gray-800 mb-3">
            <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" className="text-gray-400" aria-hidden="true">
              <line x1="6" y1="3" x2="6" y2="15" />
              <circle cx="18" cy="6" r="3" />
              <circle cx="6" cy="18" r="3" />
              <path d="M18 9a9 9 0 0 1-9 9" />
            </svg>
          </div>
          <p className="text-sm font-medium text-gray-900 dark:text-white">
            Nenhuma regra de split configurada
          </p>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
            Configure como o valor é dividido entre os recebedores.
          </p>
          <Link
            href="/split-rules/new"
            className="inline-flex items-center mt-4 h-9 rounded-lg bg-brand-500 hover:bg-brand-600 px-4 text-sm font-semibold text-white transition-colors"
          >
            + Criar primeira regra
          </Link>
        </div>
      ) : (
        // Lista flat estilo /transactions — single ul rounded-2xl com divide-y.
        // Cada rule é um row compacto; clique no row (ou na chevron) expande
        // a seção de recipients (que antes era visível direto, pesando o card).
        <ul className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800 overflow-hidden">
          {rules.map((rule) => {
            const isOwner = mySellerId !== null && rule.ownerSellerId === mySellerId;
            const ownerInRecipients = rule.recipients.some((r) => r.sellerId === rule.ownerSellerId);
            const totalPct = rule.recipients.reduce((sum, r) => sum + r.percentage, 0);
            const totalFixed = rule.recipients.reduce((sum, r) => sum + r.fixedAmount, 0);
            const residualPct = Math.max(0, 100 - totalPct);
            const showOwnerResidual = isOwner && !ownerInRecipients;
            const recipientCount = rule.recipients.length + (showOwnerResidual ? 1 : 0);
            const isExpanded = expandedRuleIds.has(rule.id);
            return (
              <li key={rule.id}>
                {/* Row clicável (header da rule) — abre/fecha recipients. */}
                <button
                  type="button"
                  onClick={() => toggleExpanded(rule.id)}
                  className="w-full flex items-center gap-3 px-5 py-4 text-left hover:bg-gray-50/60 dark:hover:bg-white/[0.02] transition-colors"
                  aria-expanded={isExpanded}
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2 mb-0.5 flex-wrap">
                      <p className="text-sm font-semibold text-gray-900 dark:text-white truncate">
                        {rule.name}
                      </p>
                      <span
                        className={`inline-flex items-center gap-1 rounded-md px-2 py-0.5 text-[10px] uppercase tracking-wider font-semibold ${
                          rule.isActive
                            ? "bg-success-50 text-success-700 dark:bg-success-500/15 dark:text-success-400"
                            : "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400"
                        }`}
                      >
                        {rule.isActive ? "Ativa" : "Rascunho"}
                      </span>
                    </div>
                    <p className="text-xs text-gray-500 dark:text-gray-400">
                      {isOwner ? "Sua regra" : `Regra de ${rule.ownerSellerName ?? "outro seller"}`}
                      <span className="mx-1.5 text-gray-300 dark:text-gray-700">·</span>
                      {recipientCount} {recipientCount === 1 ? "destinatário" : "destinatários"}
                      <span className="mx-1.5 text-gray-300 dark:text-gray-700">·</span>
                      criada em {formatDate(rule.createdAt)}
                    </p>
                  </div>

                  {/* Ações inline à direita do row (só pro owner) — stopPropagation
                      para não acionar o toggle do expand. */}
                  {isOwner && (
                    <div className="flex items-center gap-1.5 shrink-0">
                      {!rule.isActive ? (
                        <span
                          role="button"
                          tabIndex={0}
                          onClick={(e) => { e.stopPropagation(); handleActivate(rule.id); }}
                          onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); e.stopPropagation(); handleActivate(rule.id); } }}
                          className="h-8 inline-flex items-center rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-xs font-semibold text-white transition-colors cursor-pointer"
                        >
                          Ativar
                        </span>
                      ) : (
                        <span
                          role="button"
                          tabIndex={0}
                          aria-label={`Desativar regra ${rule.name}`}
                          title="Desativar"
                          onClick={(e) => { e.stopPropagation(); askDeactivate(rule.id, rule.name); }}
                          onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); e.stopPropagation(); askDeactivate(rule.id, rule.name); } }}
                          className="inline-flex items-center justify-center h-8 w-8 rounded-lg text-gray-500 hover:bg-error-50 hover:text-error-600 dark:text-gray-400 dark:hover:bg-error-500/10 dark:hover:text-error-400 transition-colors cursor-pointer"
                        >
                          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                            <circle cx="12" cy="12" r="10" />
                            <line x1="4.93" y1="4.93" x2="19.07" y2="19.07" />
                          </svg>
                        </span>
                      )}
                    </div>
                  )}

                  {/* Chevron expand indicator */}
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
                    className={`shrink-0 text-gray-400 dark:text-gray-500 transition-transform ${isExpanded ? "rotate-180" : ""}`}
                  >
                    <polyline points="6 9 12 15 18 9" />
                  </svg>
                </button>

                {/* Bloco expandido — recipients detalhados, igual ao card antigo. */}
                {isExpanded && (
                  <div className="px-5 pb-4 bg-gray-50/40 dark:bg-white/[0.01] border-t border-gray-100 dark:border-gray-800/50">
                    {rule.recipients
                      .slice()
                      .sort((a, b) => a.priority - b.priority)
                      .map((r) => (
                        <RecipientRow key={r.id} recipient={r} mySellerId={mySellerId} />
                      ))}
                    {showOwnerResidual && (
                      <div className="flex items-start gap-3 py-3 border-b border-gray-100 last:border-0 dark:border-gray-800">
                        <span className="shrink-0 inline-flex items-center justify-center w-8 h-8 rounded-full text-xs font-semibold bg-brand-500 text-white">
                          V
                        </span>
                        <div className="flex-1 min-w-0">
                          <p className="text-sm font-medium text-gray-900 dark:text-white">Você</p>
                          <p className="text-[11px] text-gray-500 dark:text-gray-400">residual da regra</p>
                        </div>
                        <div className="shrink-0 text-right space-y-0.5">
                          <div className="flex items-baseline justify-end gap-1">
                            <span className="text-base font-semibold text-gray-900 dark:text-white tabular-nums">
                              {residualPct}%
                            </span>
                          </div>
                          <p className="text-[11px] text-gray-500 dark:text-gray-400">
                            {totalFixed > 0 ? `menos ${formatCurrency(totalFixed)} fixos` : "do bruto"}
                          </p>
                        </div>
                      </div>
                    )}
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}

      <ConfirmModal
        isOpen={pending !== null}
        title="Desativar regra de split"
        message={`Desativar a regra "${pending?.name}"? Payment links que usarem essa regra vão recusar novos pagamentos até reativar.`}
        confirmLabel="Desativar"
        variant="danger"
        requireCode
        isLoading={pendingRunning}
        onCancel={() => { if (!pendingRunning) setPending(null); }}
        onConfirm={runDeactivate}
      />
    </div>
  );
}
