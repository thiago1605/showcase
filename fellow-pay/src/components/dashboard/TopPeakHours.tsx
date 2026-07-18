"use client";
import React from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardService } from "@/services/dashboard.service";
import { useDashboardPeriod } from "./PeriodContext";
import { EmptyStateCTA } from "./EmptyStateCTA";

const DAY_LABELS = ["Domingo", "Segunda", "Terça", "Quarta", "Quinta", "Sexta", "Sábado"];

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

/**
 * Lê do mesmo cache do HeatmapChart (queryKey idêntica) — sem novo backend.
 * Mostra os 5 buckets (DayOfWeek × Hour) com mais transações capturadas.
 */
export function TopPeakHours() {
  const { period } = useDashboardPeriod();

  const { data, isLoading, error } = useQuery({
    queryKey: ["dashboard", "heatmap", period.from, period.to],
    queryFn: () => dashboardService.getHeatmap({ from: period.from, to: period.to }),
  });

  const top = (data?.cells ?? [])
    .filter((c) => c.count > 0)
    .sort((a, b) => b.count - a.count)
    .slice(0, 5);

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full">
      <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Top 5 horários de pico</h3>
        <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
          Janelas com mais movimento (UTC)
        </p>
      </div>
      <div className="p-5 space-y-3">
        {isLoading && (
          <div className="space-y-2">
            {[...Array(3)].map((_, i) => (
              <div key={i} className="h-10 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />
            ))}
          </div>
        )}

        {error && !isLoading && (
          <p className="text-sm text-error-600 dark:text-error-400">
            {error instanceof Error ? error.message : "Erro ao carregar."}
          </p>
        )}

        {!isLoading && !error && top.length === 0 && (
          <EmptyStateCTA
            compact
            title="Sem captures no período"
            description="Os horários de pico aparecem assim que houver volume."
          />
        )}

        {!isLoading && !error && top.map((cell, i) => (
          <div key={`${cell.dayOfWeek}-${cell.hour}`} className="flex items-center gap-3">
            <span className="inline-flex items-center justify-center w-6 h-6 rounded-full bg-brand-50 text-brand-700 dark:bg-brand-500/20 dark:text-brand-300 text-xs font-semibold">
              {i + 1}
            </span>
            <div className="flex-1 min-w-0">
              <p className="text-sm font-medium text-gray-900 dark:text-white">
                {DAY_LABELS[cell.dayOfWeek]} às {cell.hour.toString().padStart(2, "0")}h
              </p>
              <p className="text-xs text-gray-500 dark:text-gray-400">
                {cell.count} {cell.count === 1 ? "transação" : "transações"} · {formatCurrency(cell.volume)}
              </p>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
