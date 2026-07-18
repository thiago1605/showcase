"use client";
import React from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardService } from "@/services/dashboard.service";
import { useDashboardPeriod } from "./PeriodContext";
import { EmptyStateCTA } from "./EmptyStateCTA";

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

function initial(name: string | null, email: string): string {
  const src = (name && name.trim()) || email;
  return src.charAt(0).toUpperCase();
}

const AVATAR_COLORS = [
  "bg-brand-500/15 text-brand-700 dark:text-brand-300",
  "bg-success-500/15 text-success-700 dark:text-success-300",
  "bg-blue-light-500/15 text-blue-light-700 dark:text-blue-light-300",
  "bg-warning-500/15 text-warning-700 dark:text-warning-300",
  "bg-orange-500/15 text-orange-700 dark:text-orange-300",
];

export function TopCustomers() {
  const { period } = useDashboardPeriod();
  const { data, isLoading: loading, error: queryError } = useQuery({
    queryKey: ["dashboard", "top-customers", period.from, period.to],
    queryFn: () => dashboardService.getTopCustomers({ from: period.from, to: period.to }, 5),
  });
  const items = data ?? [];
  const error = queryError instanceof Error ? queryError.message : queryError ? "Erro ao carregar." : null;

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full">
      <div className="flex items-center justify-between px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <div>
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Top Clientes</h3>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">Quem mais comprou no período</p>
        </div>
      </div>
      <div className="p-5 space-y-3">
        {loading && (
          <div className="space-y-3">
            {[...Array(3)].map((_, i) => (
              <div key={i} className="h-10 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />
            ))}
          </div>
        )}

        {error && <p className="text-sm text-error-600 dark:text-error-400">{error}</p>}

        {!loading && !error && items.length === 0 && (
          <EmptyStateCTA
            compact
            title="Nenhum cliente identificado"
            description="Quando seus clientes informarem nome e email no checkout, eles aparecem aqui."
          />
        )}

        {!loading && !error && items.map((c, i) => (
          <div key={c.email} className="flex items-center gap-3">
            <span className={`inline-flex items-center justify-center w-9 h-9 rounded-full font-semibold text-sm ${AVATAR_COLORS[i % AVATAR_COLORS.length]}`}>
              {initial(c.name, c.email)}
            </span>
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium text-gray-900 dark:text-white truncate">
                {c.name || c.email}
              </p>
              <p className="text-xs text-gray-500 dark:text-gray-400 truncate">
                {c.email} · {c.count} transaç{c.count === 1 ? "ão" : "ões"}
              </p>
            </div>
            <span className="text-sm font-semibold text-gray-900 dark:text-white whitespace-nowrap">
              {formatCurrency(c.volume)}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}
