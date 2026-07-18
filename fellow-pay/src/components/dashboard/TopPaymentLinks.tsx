"use client";
import React from "react";
import { useQuery } from "@tanstack/react-query";
import { dashboardService } from "@/services/dashboard.service";
import { useDashboardPeriod } from "./PeriodContext";
import { EmptyStateCTA } from "./EmptyStateCTA";

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

export function TopPaymentLinks() {
  const { period } = useDashboardPeriod();
  const { data, isLoading: loading, error: queryError } = useQuery({
    queryKey: ["dashboard", "top-payment-links", period.from, period.to],
    queryFn: () => dashboardService.getTopPaymentLinks({ from: period.from, to: period.to }, 5),
  });
  const items = data ?? [];
  const error = queryError instanceof Error ? queryError.message : queryError ? "Erro ao carregar." : null;

  // Só mostramos uma barra relativa quando há dispersão real de valores. Se todos
  // têm volume parecido (caso comum com poucos pagamentos no período), a barra
  // vira ruído visual — todas ficam ~100%. Threshold: ratio min/max > 0.95.
  const max = items.reduce((acc, x) => Math.max(acc, x.volume), 0);
  const min = items.reduce((acc, x) => Math.min(acc, x.volume), max);
  const showBars = max > 0 && min / max < 0.95 && items.length > 1;

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full">
      <div className="flex items-center justify-between px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <div>
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Principais links de pagamento</h3>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">Ranking por volume capturado</p>
        </div>
        <a
          href="/payment-links"
          className="text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400"
        >
          Ver todos
        </a>
      </div>
      <div className="p-3">
        {loading && (
          <div className="space-y-2 p-2">
            {[...Array(3)].map((_, i) => (
              <div key={i} className="h-10 bg-gray-100 dark:bg-gray-800 rounded-lg animate-pulse" />
            ))}
          </div>
        )}

        {error && <p className="text-sm text-error-600 dark:text-error-400 p-3">{error}</p>}

        {!loading && !error && items.length === 0 && (
          <div className="p-2">
            <EmptyStateCTA
              compact
              title="Nenhum link com pagamentos"
              description="Crie um link de pagamento e compartilhe com seus clientes para começar a vender."
              ctaLabel="Criar link de pagamento"
              ctaHref="/payment-links"
            />
          </div>
        )}

        {!loading && !error && items.length > 0 && (
          <ul className="divide-y divide-gray-100 dark:divide-gray-800/80">
            {items.map((item, i) => (
              <li key={item.paymentLinkId}>
                <a
                  href={`/payment-links/${item.paymentLinkId}`}
                  className="flex items-center gap-3 px-2 py-2.5 rounded-lg hover:bg-gray-50 dark:hover:bg-white/[0.03] transition-colors"
                >
                  <span className="inline-flex items-center justify-center w-7 h-7 rounded-lg bg-brand-50 text-brand-700 dark:bg-brand-500/15 dark:text-brand-300 text-[12px] font-semibold tabular-nums shrink-0">
                    {i + 1}
                  </span>
                  <div className="min-w-0 flex-1">
                    <p className="text-sm font-medium text-gray-900 dark:text-white truncate" title={item.name}>
                      {item.name}
                    </p>
                    <p className="text-xs text-gray-500 dark:text-gray-400 tabular-nums">
                      {item.count} pagto{item.count === 1 ? "" : "s"}
                    </p>
                  </div>
                  {showBars && (
                    <div className="hidden md:block w-20 h-1 rounded-full bg-gray-100 dark:bg-gray-800/80 shrink-0">
                      <div
                        className="h-1 rounded-full bg-brand-500"
                        style={{ width: `${(item.volume / max) * 100}%` }}
                      />
                    </div>
                  )}
                  <span className="text-sm font-semibold text-gray-900 dark:text-white whitespace-nowrap tabular-nums shrink-0">
                    {formatCurrency(item.volume)}
                  </span>
                </a>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}
