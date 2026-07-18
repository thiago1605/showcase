"use client";
import React, { useState, useEffect } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { DetailPageSkeleton } from "@/components/ui/Skeleton";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { BackLink } from "@/components/ui/BackLink";
import { payoutsService } from "@/services/payouts.service";
import type { Payout } from "@/types";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  if (!dateStr) return "\u2014";
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" }).format(new Date(dateStr));
}

export default function PayoutDetailPage() {
  const params = useParams();
  const id = params.id as string;
  const [payout, setPayout] = useState<Payout | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState("");

  useEffect(() => {
    async function load() {
      try {
        const data = await payoutsService.getById(id);
        setPayout(data);
      } catch {
        setError("Saque não encontrado.");
      }
      setIsLoading(false);
    }
    load();
  }, [id]);

  if (isLoading) {
    return (
      <DetailPageSkeleton ariaLabel="Carregando saque" />
    );
  }

  if (error || !payout) {
    return (
      <div className="space-y-4">
        <BackLink fallbackHref="/payouts" />
        <div className="p-4 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{error}</div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <BackLink fallbackHref="/payouts" />

      <div className="flex items-center gap-2 text-sm">
        <Link href="/payouts" className="text-brand-500 hover:text-brand-600">Saques</Link>
        <span className="text-gray-400">/</span>
        <IdDisplay id={id} />
      </div>

      <div className="flex items-start justify-between">
        <h1 className="text-xl font-semibold text-gray-900 dark:text-white">{formatCurrency(payout.amount)}</h1>
        <StatusBadge status={payout.status} kind="payout" />
      </div>

      <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
        <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-4">Detalhes do Saque</h2>
        <dl className="space-y-3 text-sm">
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">ID</dt>
            <dd><IdDisplay id={payout.id} copyable /></dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Valor solicitado</dt>
            <dd className="text-gray-900 dark:text-white">{formatCurrency(payout.amount)}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Taxa</dt>
            <dd className="text-gray-900 dark:text-white">{formatCurrency(payout.fee)}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Valor líquido</dt>
            <dd className="text-gray-900 dark:text-white font-medium">{formatCurrency(payout.amount - payout.fee)}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Solicitado em</dt>
            <dd className="text-gray-900 dark:text-white">{formatDate(payout.createdAt)}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-gray-500 dark:text-gray-400">Processado em</dt>
            <dd className="text-gray-900 dark:text-white">{formatDate(payout.processedAt)}</dd>
          </div>
        </dl>
      </div>
    </div>
  );
}
