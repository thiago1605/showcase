"use client";
import React, { useEffect, useState } from "react";
import { transactionsService } from "@/services/transactions.service";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { PaymentMethodBadge } from "@/components/ui/PaymentMethodBadge";
import type { Transaction } from "@/types";

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" });
}

export function RecentTransactions() {
  const [transactions, setTransactions] = useState<Transaction[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    transactionsService
      .list({ pageSize: 6 })
      .then((res) => setTransactions(res.items))
      .catch((err) => setError(err.message || "Erro ao carregar transações"))
      .finally(() => setLoading(false));
  }, []);

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900">
      <div className="flex items-center justify-between px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <h3 className="text-sm font-semibold text-gray-900 dark:text-white">
          Últimas Transações
        </h3>
        <a
          href="/transactions"
          className="text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400"
        >
          Ver todas
        </a>
      </div>

      {loading && (
        <div className="p-5 space-y-3">
          {[...Array(4)].map((_, i) => (
            <div key={i} className="h-10 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />
          ))}
        </div>
      )}

      {error && (
        <div className="p-5">
          <p className="text-sm text-error-600 dark:text-error-400">{error}</p>
        </div>
      )}

      {!loading && !error && transactions.length === 0 && (
        <div className="p-5">
          <p className="text-sm text-gray-500 dark:text-gray-400 text-center py-4">
            Nenhuma transação encontrada
          </p>
        </div>
      )}

      {!loading && !error && transactions.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-100 dark:border-gray-800">
                <th className="px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400">Cliente</th>
                <th className="px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400">Valor</th>
                <th className="px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400">Método</th>
                <th className="px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400">Status</th>
                <th className="px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400">Data</th>
              </tr>
            </thead>
            <tbody>
              {transactions.map((tx) => (
                <tr key={tx.id} className="border-b border-gray-50 last:border-0 dark:border-gray-800/50">
                  <td className="px-5 py-3 text-gray-800 dark:text-gray-200">
                    {tx.payerName || tx.description || (
                      <span className="italic text-gray-400 dark:text-gray-500">Cliente não informado</span>
                    )}
                  </td>
                  <td className="px-5 py-3 font-medium text-gray-900 dark:text-white">
                    {formatCurrency(tx.amount)}
                  </td>
                  <td className="px-5 py-3">
                    <PaymentMethodBadge type={tx.paymentType} />
                  </td>
                  <td className="px-5 py-3">
                    <StatusBadge status={tx.status} kind="transaction" />
                  </td>
                  <td className="px-5 py-3 text-gray-500 dark:text-gray-400 text-xs">
                    {formatDate(tx.createdAt)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
