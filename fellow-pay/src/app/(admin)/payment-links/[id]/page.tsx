"use client";
import React, { useState, useEffect } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { paymentLinksService } from "@/services/payment-links.service";
import { splitRulesService } from "@/services/split-rules.service";
import { PaymentMethodBadge } from "@/components/ui/PaymentMethodBadge";
import { DetailPageSkeleton } from "@/components/ui/Skeleton";
import { ConfirmModal } from "@/components/ui/ConfirmModal";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { BackLink } from "@/components/ui/BackLink";
import type { PaymentLink, SplitRule } from "@/types";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  if (!dateStr) return "\u2014";
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" }).format(new Date(dateStr));
}

export default function PaymentLinkDetailPage() {
  const params = useParams();
  const id = params.id as string;
  const [link, setLink] = useState<PaymentLink | null>(null);
  const [splitRule, setSplitRule] = useState<Pick<SplitRule, "id" | "name" | "isActive"> | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState("");
  const [copied, setCopied] = useState(false);
  const [confirmingDeactivate, setConfirmingDeactivate] = useState(false);
  const [deactivateRunning, setDeactivateRunning] = useState(false);

  useEffect(() => {
    async function load() {
      try {
        const data = await paymentLinksService.getById(id);
        setLink(data);
        // Lookup the rule's name. Best-effort: if the seller can't read the rule (rare —
        // usually they own it since they could attach it), we fall back to the short id.
        if (data.splitRuleId) {
          try {
            const rule = await splitRulesService.getById(data.splitRuleId);
            setSplitRule({ id: rule.id, name: rule.name, isActive: rule.isActive });
          } catch {
            setSplitRule(null);
          }
        }
      } catch {
        setError("Payment link não encontrado.");
      }
      setIsLoading(false);
    }
    load();
  }, [id]);

  const handleCopy = () => {
    if (link) {
      navigator.clipboard.writeText(link.url);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }
  };

  const runDeactivate = async () => {
    setDeactivateRunning(true);
    try {
      await paymentLinksService.deactivate(id);
      const updated = await paymentLinksService.getById(id);
      setLink(updated);
      setConfirmingDeactivate(false);
    } catch {
      setError("Erro ao desativar link.");
    }
    setDeactivateRunning(false);
  };

  if (isLoading) {
    return <DetailPageSkeleton ariaLabel="Carregando link de pagamento" />;
  }

  if (error || !link) {
    return (
      <div className="space-y-4">
        <BackLink fallbackHref="/payment-links" />
        <div className="p-4 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{error}</div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <BackLink fallbackHref="/payment-links" />

      <div className="flex items-center gap-2 text-sm">
        <Link href="/payment-links" className="text-brand-500 hover:text-brand-600">Links de pagamento</Link>
        <span className="text-gray-400">/</span>
        {link.description ? (
          <span className="text-gray-600 dark:text-gray-400">{link.description}</span>
        ) : (
          <IdDisplay id={id} />
        )}
      </div>

      <div className="flex items-start justify-between">
        <div>
          <h1 className="text-xl font-semibold text-gray-900 dark:text-white">{link.description || "Link de pagamento"}</h1>
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">{formatCurrency(link.amount)}</p>
        </div>
        <div className="flex items-center gap-3">
          <span className={`inline-flex rounded-full px-2 py-0.5 text-xs font-medium ${link.active ? "bg-success-50 text-success-700 dark:bg-success-500/10 dark:text-success-400" : "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400"}`}>
            {link.active ? "Ativo" : "Inativo"}
          </span>
          {link.active && (
            <button onClick={() => setConfirmingDeactivate(true)} className="rounded-lg border border-gray-200 px-3 py-1.5 text-xs font-medium text-gray-600 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-400">
              Desativar
            </button>
          )}
        </div>
      </div>

      {/* Copyable URL */}
      <div className="rounded-xl border border-gray-200 bg-white p-4 dark:border-gray-800 dark:bg-gray-900">
        <label className="block text-xs font-medium text-gray-500 dark:text-gray-400 mb-2">URL do Link</label>
        <div className="flex items-center gap-2">
          <input type="text" value={link.url} readOnly className="flex-1 rounded-lg border border-gray-200 bg-gray-50 px-3 py-2 text-sm font-mono dark:border-gray-700 dark:bg-gray-800 dark:text-white" />
          <button onClick={handleCopy} className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 transition-colors">
            {copied ? "Copiado!" : "Copiar"}
          </button>
        </div>
      </div>

      <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
        <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-4">Detalhes</h2>
        <dl className="space-y-3 text-sm">
          <div className="flex justify-between items-center">
            <dt className="text-gray-500 dark:text-gray-400">Método</dt>
            <dd>
              {link.paymentTypes && link.paymentTypes.length > 1 ? (
                <div className="flex flex-wrap gap-1 justify-end">
                  {link.paymentTypes.map((t) => (
                    <PaymentMethodBadge key={String(t)} type={t} />
                  ))}
                </div>
              ) : (
                <PaymentMethodBadge type={link.paymentType} />
              )}
            </dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Parcelas</dt>
            <dd className="text-gray-900 dark:text-white">{link.installments}x</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Usos</dt>
            <dd className="text-gray-900 dark:text-white">{link.usageCount}/{link.maxUses || "\u221E"}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Expira em</dt>
            <dd className="text-gray-900 dark:text-white">{link.expiresAt ? formatDate(link.expiresAt) : "Sem expiração"}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Criado em</dt>
            <dd className="text-gray-900 dark:text-white">{formatDate(link.createdAt)}</dd>
          </div>
        </dl>
      </div>

      {link.splitRuleId && (
        <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
          <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-1">Regra de split aplicada</h2>
          <p className="text-xs text-gray-500 dark:text-gray-400 mb-3">
            Cada pagamento deste link gera uma transação que aplica esta regra. Vendas anteriores não são alteradas se a regra mudar.
          </p>
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-3">
              {splitRule ? (
                <Link href={`/split-rules`} className="text-sm font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400">
                  {splitRule.name}
                </Link>
              ) : (
                <IdDisplay id={link.splitRuleId} copyable />
              )}
              {splitRule && !splitRule.isActive && (
                <span className="inline-flex items-center rounded-full bg-warning-50 px-2 py-0.5 text-xs font-medium text-warning-700 dark:bg-warning-500/10 dark:text-warning-400">
                  Inativa — novos pagamentos vão falhar
                </span>
              )}
            </div>
          </div>
        </div>
      )}

      <ConfirmModal
        isOpen={confirmingDeactivate}
        title="Desativar payment link"
        message="Desativar este link? Novos pagamentos serão recusados até reativar."
        confirmLabel="Desativar"
        variant="danger"
        requireCode
        isLoading={deactivateRunning}
        onCancel={() => { if (!deactivateRunning) setConfirmingDeactivate(false); }}
        onConfirm={runDeactivate}
      />
    </div>
  );
}
