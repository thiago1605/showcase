"use client";
import React, { useMemo } from "react";
import dynamic from "next/dynamic";
import type { ApexOptions } from "apexcharts";
import { useDashboardSummary } from "./useDashboardSummary";
import { useChartTheme } from "./useChartTheme";
import { EmptyStateCTA } from "./EmptyStateCTA";
import { paymentTypeKey, paymentTypeLabel } from "@/lib/formatters/enums";

const ReactApexChart = dynamic(() => import("react-apexcharts"), { ssr: false });

// Mantida em sincronia com `<PaymentMethodBadge>` e `PaymentMethodChart` antigo
// — paleta única para etiquetas, gráfico e chips do checkout.
// Dark = end do gradient (Tailwind ~600, um pouco mais clarinhas que -700).
const METHOD_COLORS: Record<string, string> = {
  PIX: "#16A34A",          // green-600
  CREDIT_CARD: "#8B47D9",  // purple um shade mais claro que pill-brand #7029bd
  DEBIT_CARD: "#2563EB",   // blue-600
  BOLETO: "#EA580C",       // orange-600
};

// Light = start do gradient (top-left). Tailwind ~300 do mesmo hue.
const METHOD_COLORS_LIGHT: Record<string, string> = {
  PIX: "#86EFAC",          // green-300
  CREDIT_CARD: "#b07ae0",  // mesmo light do pill-brand
  DEBIT_CARD: "#93C5FD",   // blue-300
  BOLETO: "#FDBA74",       // orange-300
};

// Ícones outline (24x24) por método — usados quando renderizamos um único
// método em destaque (caso degenerado do donut). Mantém consistência com a
// linguagem visual dos badges sem precisar reinventar a paleta.
function MethodIcon({ method, size = 28 }: { method: string; size?: number }) {
  const stroke = "currentColor";
  const common = { width: size, height: size, viewBox: "0 0 24 24", fill: "none", stroke, strokeWidth: 1.8, strokeLinecap: "round" as const, strokeLinejoin: "round" as const, "aria-hidden": true as const };
  switch (method) {
    case "PIX":
      // Losango PIX (sugere o símbolo oficial do Banco Central).
      return (
        <svg {...common}>
          <path d="M12 3 21 12l-9 9-9-9 9-9Z" />
          <path d="m8 12 4 4 4-4-4-4-4 4Z" />
        </svg>
      );
    case "CREDIT_CARD":
      // Cartão com tarja magnética + chip.
      return (
        <svg {...common}>
          <rect x="3" y="6" width="18" height="13" rx="2" />
          <line x1="3" y1="10" x2="21" y2="10" />
          <line x1="7" y1="15" x2="11" y2="15" />
        </svg>
      );
    case "DEBIT_CARD":
      // Cartão débito — variante com bandeira/identificação na lateral.
      return (
        <svg {...common}>
          <rect x="3" y="6" width="18" height="13" rx="2" />
          <line x1="3" y1="10" x2="21" y2="10" />
          <line x1="14" y1="15" x2="18" y2="15" />
          <circle cx="7" cy="15" r="1.2" />
        </svg>
      );
    case "BOLETO":
      // Código de barras vertical.
      return (
        <svg {...common}>
          <line x1="5" y1="5" x2="5" y2="19" />
          <line x1="8" y1="5" x2="8" y2="19" strokeWidth="2.5" />
          <line x1="11" y1="5" x2="11" y2="19" />
          <line x1="13" y1="5" x2="13" y2="19" strokeWidth="2.5" />
          <line x1="16" y1="5" x2="16" y2="19" />
          <line x1="19" y1="5" x2="19" y2="19" strokeWidth="2.5" />
        </svg>
      );
    default:
      return null;
  }
}

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

export function PaymentMethodChart() {
  const { current, loading, error } = useDashboardSummary();
  const chartTheme = useChartTheme();

  const { labels, series, colors, colorsDark, totalVolume, top, methodRows } = useMemo(() => {
    if (!current) return { labels: [], series: [], colors: [], colorsDark: [], totalVolume: 0, top: null as null | { label: string; volume: number; pct: number }, methodRows: [] as { key: string; label: string; volume: number; color: string; count: number; pct: number }[] };

    const items = current.byPaymentType
      .map((p) => {
        const key = paymentTypeKey(p.paymentType) ?? String(p.paymentType);
        return {
          key,
          label: paymentTypeLabel(p.paymentType),
          volume: p.volume,
          count: p.count,
          color: METHOD_COLORS[key] ?? "#9CA3AF",
          colorLight: METHOD_COLORS_LIGHT[key] ?? "#D1D5DB",
        };
      })
      .filter((x) => x.volume > 0)
      .sort((a, b) => b.volume - a.volume);

    const totalVolume = items.reduce((acc, x) => acc + x.volume, 0);
    const top = items.length > 0
      ? { label: items[0].label, volume: items[0].volume, pct: totalVolume > 0 ? Math.round((items[0].volume / totalVolume) * 100) : 0 }
      : null;

    const methodRows = items.map((x) => ({
      ...x,
      pct: totalVolume > 0 ? Math.round((x.volume / totalVolume) * 100) : 0,
    }));

    return {
      labels: items.map((x) => x.label),
      series: items.map((x) => x.volume),
      // colors = light (start do gradient), colorsDark = saturado (end).
      colors: items.map((x) => x.colorLight),
      colorsDark: items.map((x) => x.color),
      totalVolume,
      top,
      methodRows,
    };
  }, [current]);

  const options: ApexOptions = useMemo(
    () => ({
      chart: { fontFamily: "Outfit, sans-serif", type: "bar", toolbar: { show: false } },
      colors,
      xaxis: {
        categories: labels,
        labels: {
          style: { colors: chartTheme.muted, fontSize: "11px" },
          rotate: 0,
          hideOverlappingLabels: true,
        },
        axisBorder: { show: false },
        axisTicks: { show: false },
        // Sem faixa fantasma vertical no hover (ver TransactionsByStatus).
        crosshairs: { show: false },
        tooltip: { enabled: false },
      },
      states: {
        hover: { filter: { type: "none" } },
        active: { filter: { type: "none" } },
      },
      yaxis: {
        labels: {
          style: { colors: chartTheme.muted, fontSize: "11px" },
          formatter: (val: number) => `R$ ${Math.round(val).toLocaleString("pt-BR")}`,
        },
      },
      legend: { show: false },
      dataLabels: { enabled: false },
      stroke: { width: 0 },
      // borderRadius 14: pill consistente em todas as alturas. Acima de 14,
      // métodos com volume baixo (Pix com 1-2 tx vs Cartão com 10+) viravam
      // "ovo" porque a curva consumia toda a altura da barra menor.
      plotOptions: {
        bar: {
          columnWidth: "55%",
          borderRadius: 14,
          borderRadiusApplication: "around",
          distributed: true, // cor por barra (colors[])
        },
      },
      // Gradient diagonal2: highlight glossy no canto top-right + cor vívida
      // saturada nos outros 80% da barra.
      // Pill-style: light start (top-left) → dark end (bottom-right).
      fill: {
        type: "gradient",
        gradient: {
          type: "diagonal1",
          gradientToColors: colorsDark,
          opacityFrom: 1,
          opacityTo: 1,
          stops: [0, 100],
        },
      },
      grid: {
        borderColor: chartTheme.grid,
        strokeDashArray: 3,
        yaxis: { lines: { show: true } },
        xaxis: { lines: { show: false } },
      },
      tooltip: {
        theme: chartTheme.isDark ? "dark" : "light",
        // Custom HTML — mesmo motivo de TransactionsByStatus: distributed:true
        // + 1 série única faz o marker padrão (e marker.fillColors) sempre
        // puxar índice 0. Custom indexa por dataPointIndex corretamente.
        custom: ({ series, dataPointIndex, w }: { series: number[][]; dataPointIndex: number; w: { globals: { labels: string[] } } }) => {
          const label = w.globals.labels[dataPointIndex] ?? "";
          const val = series[0][dataPointIndex] ?? 0;
          const color = colorsDark[dataPointIndex] ?? "#9CA3AF";
          return `
            <div class="apexcharts-tooltip-title">${label}</div>
            <div class="apexcharts-tooltip-series-group apexcharts-active" style="display:flex; align-items:center; gap:8px;">
              <span class="apexcharts-tooltip-marker" style="background:${color}; width:8px; height:8px; border-radius:50%; display:inline-block;"></span>
              <div class="apexcharts-tooltip-text">
                <div class="apexcharts-tooltip-y-group">
                  <span class="apexcharts-tooltip-text-y-label">Volume: </span>
                  <span class="apexcharts-tooltip-text-y-value">${formatCurrency(val)}</span>
                </div>
              </div>
            </div>
          `;
        },
      },
    }),
    [labels, colors, colorsDark, chartTheme]
  );

  if (loading) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full animate-pulse">
        <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
          <div className="h-4 w-36 bg-gray-200 dark:bg-gray-700 rounded" />
        </div>
        <div className="p-5">
          <div className="h-60 bg-gray-100 dark:bg-gray-800 rounded" />
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full">
        <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Métodos de Pagamento</h3>
        </div>
        <div className="p-5">
          <p className="text-sm text-error-600 dark:text-error-400">{error}</p>
        </div>
      </div>
    );
  }

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full">
      <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Métodos de Pagamento</h3>
        <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">Distribuição do volume</p>
      </div>
      <div className="p-5">
        {totalVolume === 0 ? (
          <EmptyStateCTA
            title="Nenhum pagamento no período"
            description="Habilite Pix, cartão e boleto na sua conta para começar a receber."
            ctaLabel="Configurar conta"
            ctaHref="/sellers"
          />
        ) : methodRows.length === 1 ? (
          // Único método ativo: donut de 100% é desproporcional. Mostramos um tile
          // limpo com totalizador e um indicador discreto da cor do método.
          <div
            role="img"
            aria-label={`Único método com movimento: ${methodRows[0].label}, ${formatCurrency(methodRows[0].volume)}.`}
            className="flex flex-col items-center justify-center py-10 px-4 rounded-xl bg-gray-50 dark:bg-gray-800/40"
          >
            <span
              className="inline-flex items-center justify-center w-14 h-14 rounded-2xl text-white shadow-sm mb-4"
              style={{ backgroundColor: methodRows[0].color }}
              aria-hidden="true"
            >
              <MethodIcon method={methodRows[0].key} size={28} />
            </span>
            <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Único método</p>
            <p className="mt-1 text-base font-semibold text-gray-900 dark:text-white">{methodRows[0].label}</p>
            <p className="mt-3 text-2xl font-semibold text-gray-900 dark:text-white tabular-nums tracking-tight">
              {formatCurrency(methodRows[0].volume)}
            </p>
            <p className="mt-1 text-xs text-gray-500 dark:text-gray-400 tabular-nums">
              {methodRows[0].count} transaç{methodRows[0].count === 1 ? "ão" : "ões"} · 100% do volume
            </p>
          </div>
        ) : (
          <>
            <div role="img" aria-label={`Distribuição de volume por método de pagamento. Total ${formatCurrency(totalVolume)}.`}>
              <ReactApexChart
                options={options}
                series={[{ name: "Volume", data: series }]}
                type="bar"
                height={260}
              />
            </div>
            {top && (
              <div className="pt-3 border-t border-gray-100 dark:border-gray-800 text-center">
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Método mais usado</p>
                <p className="text-sm font-semibold text-gray-900 dark:text-white">
                  {top.label} <span className="text-brand-600 dark:text-brand-400">({top.pct}%)</span>
                </p>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
