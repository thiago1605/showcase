"use client";
import React, { useState, useEffect } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { DetailPageSkeleton } from "@/components/ui/Skeleton";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { BackLink } from "@/components/ui/BackLink";
import { subscriptionsService } from "@/services/subscriptions.service";
import type { Subscription } from "@/types";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  if (!dateStr) return "\u2014";
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" }).format(new Date(dateStr));
}

const intervalLabels: Record<string, string> = {
  WEEKLY: "Semanal",
  MONTHLY: "Mensal",
  QUARTERLY: "Trimestral",
  YEARLY: "Anual",
};

export default function SubscriptionDetailPage() {
  const params = useParams();
  const id = params.id as string;
  const [subscription, setSubscription] = useState<Subscription | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState("");
  const [actionLoading, setActionLoading] = useState(false);

  useEffect(() => {
    async function load() {
      try {
        const data = await subscriptionsService.getById(id);
        setSubscription(data);
      } catch {
        setError("Assinatura não encontrada.");
      }
      setIsLoading(false);
    }
    load();
  }, [id]);

  const handleAction = async (action: "cancel" | "pause" | "resume") => {
    setActionLoading(true);
    try {
      if (action === "cancel") await subscriptionsService.cancel(id);
      else if (action === "pause") await subscriptionsService.pause(id);
      else await subscriptionsService.resume(id);
      const updated = await subscriptionsService.getById(id);
      setSubscription(updated);
    } catch {
      setError("Erro ao executar ação.");
    }
    setActionLoading(false);
  };

  if (isLoading) {
    return (
      <DetailPageSkeleton ariaLabel="Carregando assinatura" />
    );
  }

  if (error && !subscription) {
    return (
      <div className="space-y-4">
        <BackLink fallbackHref="/subscriptions" />
        <div className="p-4 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{error}</div>
      </div>
    );
  }

  if (!subscription) return null;

  return (
    <div className="space-y-6">
      <BackLink fallbackHref="/subscriptions" />

      <div className="flex items-center gap-2 text-sm">
        <Link href="/subscriptions" className="text-brand-500 hover:text-brand-600">Assinaturas</Link>
        <span className="text-gray-400">/</span>
        <IdDisplay id={id} />
      </div>

      <div className="flex items-start justify-between">
        <div>
          <h1 className="text-xl font-semibold text-gray-900 dark:text-white">{subscription.customerName}</h1>
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">{subscription.description}</p>
        </div>
        <div className="flex items-center gap-3">
          <StatusBadge status={subscription.status} kind="subscription" />
          {subscription.status === "ACTIVE" && (
            <button onClick={() => handleAction("pause")} disabled={actionLoading} className="rounded-lg border border-warning-200 px-3 py-1.5 text-xs font-medium text-warning-700 hover:bg-warning-50 dark:border-warning-800 dark:text-warning-400 disabled:opacity-50">
              Pausar
            </button>
          )}
          {subscription.status === "PAUSED" && (
            <button onClick={() => handleAction("resume")} disabled={actionLoading} className="rounded-lg border border-success-200 px-3 py-1.5 text-xs font-medium text-success-700 hover:bg-success-50 dark:border-success-800 dark:text-success-400 disabled:opacity-50">
              Retomar
            </button>
          )}
          {["ACTIVE", "PAUSED"].includes(subscription.status) && (
            <button onClick={() => handleAction("cancel")} disabled={actionLoading} className="rounded-lg border border-error-200 px-3 py-1.5 text-xs font-medium text-error-700 hover:bg-error-50 dark:border-error-800 dark:text-error-400 disabled:opacity-50">
              Cancelar
            </button>
          )}
        </div>
      </div>

      {error && (
        <div className="p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{error}</div>
      )}

      <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
        <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-4">Detalhes da Assinatura</h2>
        <dl className="space-y-3 text-sm">
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">ID</dt>
            <dd><IdDisplay id={subscription.id} copyable /></dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Valor</dt>
            <dd className="text-gray-900 dark:text-white font-medium">{formatCurrency(subscription.amount)}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Intervalo</dt>
            <dd className="text-gray-900 dark:text-white">{intervalLabels[subscription.interval] || subscription.interval}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Ciclos realizados</dt>
            <dd className="text-gray-900 dark:text-white">{subscription.cycleCount}/{subscription.maxCycles || "\u221E"}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Próxima cobrança</dt>
            <dd className="text-gray-900 dark:text-white">{formatDate(subscription.nextBillingDate)}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Criada em</dt>
            <dd className="text-gray-900 dark:text-white">{formatDate(subscription.createdAt)}</dd>
          </div>
        </dl>
      </div>
    </div>
  );
}
