"use client";
import React, { useState, useEffect } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { DetailPageSkeleton } from "@/components/ui/Skeleton";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { BackLink } from "@/components/ui/BackLink";
import { disputesService, Dispute } from "@/services/disputes.service";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  if (!dateStr) return "\u2014";
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" }).format(new Date(dateStr));
}

export default function DisputeDetailPage() {
  const params = useParams();
  const id = params.id as string;
  const [dispute, setDispute] = useState<Dispute | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState("");

  useEffect(() => {
    async function load() {
      try {
        const data = await disputesService.getById(id);
        setDispute(data);
      } catch {
        setError("Disputa não encontrada.");
      }
      setIsLoading(false);
    }
    load();
  }, [id]);

  if (isLoading) {
    return (
      <DetailPageSkeleton ariaLabel="Carregando disputa" />
    );
  }

  if (error || !dispute) {
    return (
      <div className="space-y-4">
        <BackLink fallbackHref="/disputes" />
        <div className="p-4 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{error}</div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <BackLink fallbackHref="/disputes" />

      <div className="flex items-center gap-2 text-sm">
        <Link href="/disputes" className="text-brand-500 hover:text-brand-600">Disputas</Link>
        <span className="text-gray-400">/</span>
        <IdDisplay id={id} />
      </div>

      <div className="flex items-start justify-between">
        <h1 className="text-xl font-semibold text-gray-900 dark:text-white">{formatCurrency(dispute.amount)}</h1>
        <StatusBadge status={dispute.status} kind="dispute" />
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
        <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
          <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-4">Detalhes da Disputa</h2>
          <dl className="space-y-3 text-sm">
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">ID</dt>
              <dd><IdDisplay id={dispute.id} copyable /></dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">Transação</dt>
              <dd>
                <Link href={`/transactions/${dispute.transactionId}`} className="text-brand-500 hover:text-brand-600">
                  <IdDisplay id={dispute.transactionId} />
                </Link>
              </dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">Valor disputado</dt>
              <dd className="text-gray-900 dark:text-white font-medium">{formatCurrency(dispute.amount)}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">Motivo</dt>
              <dd className="text-gray-900 dark:text-white">{dispute.reason}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">Aberta em</dt>
              <dd className="text-gray-900 dark:text-white">{formatDate(dispute.createdAt)}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-gray-500 dark:text-gray-400">Prazo para resposta</dt>
              <dd className="text-error-600 dark:text-error-400 font-medium">{formatDate(dispute.deadline)}</dd>
            </div>
          </dl>
        </div>

        <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
          <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-4">Evidências</h2>
          <p className="text-sm text-gray-500 dark:text-gray-400">
            Envie documentos e evidências para contestar esta disputa antes do prazo limite.
          </p>
          <div className="mt-4 p-4 border-2 border-dashed border-gray-200 dark:border-gray-700 rounded-lg text-center">
            <p className="text-xs text-gray-400">Upload de evidências disponível após integração completa com a API.</p>
          </div>
        </div>
      </div>
    </div>
  );
}
