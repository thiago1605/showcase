"use client";
import React, { useEffect, useState } from "react";
import { payoutsService } from "@/services/payouts.service";
import { payoutStatusLabel } from "@/lib/formatters/enums";
import type { Payout } from "@/types";

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleDateString("pt-BR", { day: "2-digit", month: "2-digit" });
}

export function UpcomingPayouts() {
  const [payouts, setPayouts] = useState<Payout[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    payoutsService
      .list({ pageSize: 5, status: "PENDING" })
      .then((res) => setPayouts(res.items))
      .catch((err) => setError(err.message || "Erro ao carregar recebimentos"))
      .finally(() => setLoading(false));
  }, []);

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full">
      <div className="flex items-center justify-between px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <h3 className="text-sm font-semibold text-gray-900 dark:text-white">
          Próximos Recebimentos
        </h3>
        <a
          href="/payouts"
          className="text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400"
        >
          Ver todos
        </a>
      </div>
      <div className="p-5 space-y-3">
        {loading && (
          <div className="space-y-3">
            {[...Array(3)].map((_, i) => (
              <div key={i} className="h-12 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />
            ))}
          </div>
        )}

        {error && (
          <p className="text-sm text-error-600 dark:text-error-400">{error}</p>
        )}

        {!loading && !error && payouts.length === 0 && (
          <p className="text-sm text-gray-500 dark:text-gray-400 text-center py-4">
            Nenhum recebimento agendado
          </p>
        )}

        {!loading && !error && payouts.map((payout) => (
          <div key={payout.id} className="flex items-center justify-between py-2">
            <div>
              <p className="text-sm font-medium text-gray-800 dark:text-gray-200">
                {formatCurrency(payout.amount)}
              </p>
              <p className="text-xs text-gray-500 dark:text-gray-400">
                {formatDate(payout.createdAt)}
              </p>
            </div>
            <span className="text-xs font-medium text-brand-600 dark:text-brand-400 bg-brand-50 dark:bg-brand-500/10 px-2 py-0.5 rounded-full">
              {payoutStatusLabel(payout.status)}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}
