"use client";
import React from "react";
import Image from "next/image";
import { useQuery } from "@tanstack/react-query";
import { dashboardService } from "@/services/dashboard.service";
import { useDashboardPeriod } from "./PeriodContext";
import { EmptyStateCTA } from "./EmptyStateCTA";

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

/**
 * Ranking dos produtos mais vendidos no período (top 5 por volume capturado).
 * Espelha o look do TopPaymentLinks, mas usa thumbnail (cover image) no lugar
 * do rank number quando o produto tem capa cadastrada — fica mais fácil
 * reconhecer visualmente. Quando não tem capa, fallback pro pill numerado.
 *
 * Link de cada item navega pra `/products/[id]` (gerencial), e o CTA do empty
 * state empurra pra criação de produto.
 */
export function TopProducts() {
  const { period } = useDashboardPeriod();
  const { data, isLoading: loading, error: queryError } = useQuery({
    queryKey: ["dashboard", "top-products", period.from, period.to],
    queryFn: () => dashboardService.getTopProducts({ from: period.from, to: period.to }, 5),
  });
  const items = data ?? [];
  const error = queryError instanceof Error ? queryError.message : queryError ? "Erro ao carregar." : null;

  // Mesma heurística do TopPaymentLinks: a share-bar só ajuda quando há
  // dispersão real entre valores. Sem isso, todas viram ~100% e a barra
  // é só ruído.
  const max = items.reduce((acc, x) => Math.max(acc, x.volume), 0);
  const min = items.reduce((acc, x) => Math.min(acc, x.volume), max);
  const showBars = max > 0 && min / max < 0.95 && items.length > 1;

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full">
      <div className="flex items-center justify-between px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <div>
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Produtos mais vendidos</h3>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">Ranking por volume capturado</p>
        </div>
        <a
          href="/products"
          className="text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400"
        >
          Ver todos
        </a>
      </div>
      <div className="p-3">
        {loading && (
          <div className="space-y-2 p-2">
            {[...Array(3)].map((_, i) => (
              <div key={i} className="h-12 bg-gray-100 dark:bg-gray-800 rounded-lg animate-pulse" />
            ))}
          </div>
        )}

        {error && <p className="text-sm text-error-600 dark:text-error-400 p-3">{error}</p>}

        {!loading && !error && items.length === 0 && (
          <div className="p-2">
            <EmptyStateCTA
              compact
              title="Nenhum produto vendido"
              description="Cadastre seu primeiro produto e compartilhe o link de checkout pra começar a vender."
              ctaLabel="Criar produto"
              ctaHref="/products/new"
            />
          </div>
        )}

        {!loading && !error && items.length > 0 && (
          <ul className="divide-y divide-gray-100 dark:divide-gray-800/80">
            {items.map((item, i) => (
              <li key={item.productId}>
                <a
                  href={`/products/${item.productId}`}
                  className="flex items-center gap-3 px-2 py-2.5 rounded-lg hover:bg-gray-50 dark:hover:bg-white/[0.03] transition-colors"
                >
                  {/* Thumbnail (capa do produto) com fallback pro pill numerado.
                      Capa renderiza num quadrado 10x10 com radius — mantém o
                      alinhamento com TopPaymentLinks/TopCustomers (que usam
                      pill 7x7). 10x10 cabe a imagem com respiro visual. */}
                  {item.coverImageUrl ? (
                    <span className="relative inline-block w-10 h-10 rounded-lg overflow-hidden shrink-0 bg-gray-100 dark:bg-gray-800">
                      <Image
                        src={item.coverImageUrl}
                        alt={item.name}
                        fill
                        sizes="40px"
                        className="object-cover"
                        unoptimized
                      />
                    </span>
                  ) : (
                    <span className="inline-flex items-center justify-center w-10 h-10 rounded-lg bg-brand-50 text-brand-700 dark:bg-brand-500/15 dark:text-brand-300 text-[13px] font-semibold tabular-nums shrink-0">
                      {i + 1}
                    </span>
                  )}
                  <div className="min-w-0 flex-1">
                    <p className="text-sm font-medium text-gray-900 dark:text-white truncate" title={item.name}>
                      {item.name}
                    </p>
                    <p className="text-xs text-gray-500 dark:text-gray-400 tabular-nums">
                      {item.count} venda{item.count === 1 ? "" : "s"}
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
