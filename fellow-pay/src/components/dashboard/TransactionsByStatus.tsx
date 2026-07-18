"use client";
import React, { useMemo } from "react";
import dynamic from "next/dynamic";
import { useRouter } from "next/navigation";
import type { ApexOptions } from "apexcharts";
import { useDashboardSummary } from "./useDashboardSummary";
import { useDashboardPeriod, toLocalDateString } from "./PeriodContext";
import { useChartTheme } from "./useChartTheme";
import { EmptyStateCTA } from "./EmptyStateCTA";
import { transactionStatusKey, transactionStatusLabel } from "@/lib/formatters/enums";

const ReactApexChart = dynamic(() => import("react-apexcharts"), { ssr: false });

// Cor "dark" (end do gradient — Tailwind ~600, um pouco mais clarinhas
// que a versão -700 anterior, mantendo o look pill mas com mais respiro).
const STATUS_COLORS: Record<string, string> = {
  CAPTURED: "#16A34A",   // green-600
  AUTHORIZED: "#4ADE80", // green-400
  PROCESSING: "#D97706", // amber-600
  CREATED: "#F59E0B",    // amber-500
  DECLINED: "#DC2626",   // red-600
  FAILED: "#EF4444",     // red-500
  REFUNDED: "#2563EB",   // blue-600
  VOIDED: "#6B7280",     // gray-500
};

// Cor "light" (start do gradient, top-left) — Tailwind ~300 do mesmo hue.
// Contraste forte com o dark-700 = look "pill" rico.
const STATUS_COLORS_LIGHT: Record<string, string> = {
  CAPTURED: "#86EFAC",   // green-300
  AUTHORIZED: "#BBF7D0", // green-200
  PROCESSING: "#FCD34D", // amber-300
  CREATED: "#FDE68A",    // amber-200
  DECLINED: "#FCA5A5",   // red-300
  FAILED: "#FECACA",     // red-200
  REFUNDED: "#93C5FD",   // blue-300
  VOIDED: "#D1D5DB",     // gray-300
  CHARGEBACKERROR: "#B91C1C",
};

export function TransactionsByStatus() {
  const { current, loading, error } = useDashboardSummary();
  const { period } = useDashboardPeriod();
  const router = useRouter();
  const chartTheme = useChartTheme();

  // Items derivados do summary — guardamos as keys junto com labels/series pra
  // que o handler de click no donut consiga ir para /transactions com o status certo.
  const itemsForDrilldown = useMemo(() => {
    if (!current) return [] as { key: string; label: string }[];
    return current.byStatus
      .map((s) => ({
        key: transactionStatusKey(s.status) ?? String(s.status),
        label: transactionStatusLabel(s.status),
        count: s.count,
      }))
      .filter((x) => x.count > 0)
      .sort((a, b) => b.count - a.count);
  }, [current]);

  const handleSliceClick = (statusKey: string) => {
    // Datas locais — slice direto do ISO daria offset de 1 dia quando endOfDay BRT
    // vira UTC do dia seguinte (ver toLocalDateString em PeriodContext).
    const fromDate = toLocalDateString(period.from);
    const toDate = toLocalDateString(period.to);
    router.push(`/transactions?status=${encodeURIComponent(statusKey)}&from=${fromDate}&to=${toDate}`);
  };

  // colors = light starts (gradient top-left), colorsDark = saturated ends
  // (gradient bottom-right). Replica o padrão dos pills (light → dark, 135deg).
  const { labels, series, colors, colorsDark, total, captured, declined, conversion } = useMemo(() => {
    if (!current) {
      return { labels: [], series: [], colors: [], colorsDark: [], total: 0, captured: 0, declined: 0, conversion: 0 };
    }
    const items = current.byStatus
      .map((s) => {
        const key = transactionStatusKey(s.status) ?? String(s.status);
        return {
          key,
          label: transactionStatusLabel(s.status),
          count: s.count,
          color: STATUS_COLORS[key] ?? "#9CA3AF",
          colorLight: STATUS_COLORS_LIGHT[key] ?? "#D1D5DB",
        };
      })
      .filter((x) => x.count > 0)
      .sort((a, b) => b.count - a.count);

    const total = items.reduce((acc, x) => acc + x.count, 0);
    const captured = items.find((x) => x.key === "CAPTURED")?.count ?? 0;
    const declined =
      (items.find((x) => x.key === "DECLINED")?.count ?? 0) +
      (items.find((x) => x.key === "FAILED")?.count ?? 0);
    const conversion = total > 0 ? Math.round((captured / total) * 100) : 0;

    return {
      labels: items.map((x) => x.label),
      series: items.map((x) => x.count),
      colors: items.map((x) => x.colorLight), // start do gradient (top-left)
      colorsDark: items.map((x) => x.color), // end do gradient (bottom-right)
      total,
      captured,
      declined,
      conversion,
    };
  }, [current]);

  const options: ApexOptions = useMemo(
    () => ({
      chart: {
        fontFamily: "Outfit, sans-serif",
        type: "bar",
        toolbar: { show: false },
        events: {
          dataPointSelection: (_e: unknown, _ctx: unknown, config: { dataPointIndex: number }) => {
            const item = itemsForDrilldown[config.dataPointIndex];
            if (item) handleSliceClick(item.key);
          },
        },
      },
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
        // Mata a faixa fantasma vertical que o Apex desenha atrás da barra
        // hovered (default = column highlight com cor do bar atual em opacity
        // baixa). Visual sujo no nosso gradient pill.
        crosshairs: { show: false },
        // E o mini-tooltip que aparece abaixo do label do x ao hover.
        tooltip: { enabled: false },
      },
      // Desativa o filter de "lighten" que o Apex aplica na barra ao hover —
      // com gradient saturado o lighten desbota e parece bug.
      states: {
        hover: { filter: { type: "none" } },
        active: { filter: { type: "none" } },
      },
      yaxis: {
        labels: {
          style: { colors: chartTheme.muted, fontSize: "11px" },
          formatter: (val: number) => String(Math.round(val)),
        },
      },
      legend: { show: false },
      dataLabels: { enabled: false },
      stroke: { width: 0 },
      // Barras pill-shaped (topo+base totalmente arredondados) + gradient
      // vertical do escuro (topo) ao claro (base), look Dokue.
      // borderRadius 14 (era 24): a altura das barras varia conforme o valor
      // numérico — barras com count baixo (Recusada/Cancelada com 1-2) ficam
      // muito curtas. Com radius 24 + columnWidth 55%, a curva consumia toda
      // a altura dessas barras e elas viravam "ovo". 14 mantém o efeito pill
      // visível em todas as alturas sem deformar as menores.
      plotOptions: {
        bar: {
          columnWidth: "55%",
          borderRadius: 14,
          borderRadiusApplication: "around",
          distributed: true, // cor diferente por barra (vinda de `colors`)
        },
      },
      // Gradient diagonal2 + opacityFrom 0.55: highlight glossy no canto
      // superior direito, saturação total no resto.
      // Pill-style gradient: light start (top-left) → dark end (bottom-right).
      // colors[] = lights (já passados como opção `colors` acima),
      // gradientToColors[] = darks (saturados). Sem opacity tricks.
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
        // Custom HTML — `marker.fillColors` em apex indexa por seriesIndex,
        // não dataPointIndex. Com `distributed:true` temos 1 série única e
        // o marker padrão sempre puxa fillColors[0] (verde do Aprovada),
        // independente da barra hovered. Custom resolve por dataPointIndex.
        // Mantém as classes do apex pra CSS global (liquid-glass) aplicar.
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
                  <span class="apexcharts-tooltip-text-y-label">Transações: </span>
                  <span class="apexcharts-tooltip-text-y-value">${val} transações</span>
                </div>
              </div>
            </div>
          `;
        },
      },
      // eslint-disable-next-line react-hooks/exhaustive-deps -- itemsForDrilldown e period
      // são capturados pelo handler dentro de events.dataPointSelection.
    }),
    [labels, colors, colorsDark, chartTheme, itemsForDrilldown, period.from, period.to]
  );

  if (loading) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full animate-pulse">
        <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
          <div className="h-4 w-32 bg-gray-200 dark:bg-gray-700 rounded" />
        </div>
        <div className="p-5 space-y-3">
          <div className="h-48 bg-gray-100 dark:bg-gray-800 rounded" />
          <div className="grid grid-cols-3 gap-2">
            {[...Array(3)].map((_, i) => (
              <div key={i} className="h-14 bg-gray-100 dark:bg-gray-800 rounded" />
            ))}
          </div>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full">
        <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Transações por Status</h3>
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
        <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Transações por Status</h3>
        <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
          Funil de conversão e distribuição
        </p>
      </div>
      <div className="p-5">
        {total === 0 ? (
          <EmptyStateCTA
            title="Nenhuma transação no período"
            description="Comece sua jornada por um destes caminhos:"
            actions={[
              { label: "Criar link de pagamento", href: "/payment-links" },
              { label: "Criar produto", href: "/products/new" },
              { label: "Afiliar-se a um produto", href: "/affiliate-marketplace" },
            ]}
          />
        ) : (
          <>
            <div role="img" aria-label={`Distribuição de ${total} transações por status. Clique numa barra para filtrar.`}>
              <ReactApexChart
                options={options}
                series={[{ name: "Transações", data: series }]}
                type="bar"
                height={260}
              />
            </div>

            <div className="grid grid-cols-3 gap-2 pt-4 border-t border-gray-100 dark:border-gray-800">
              <div className="text-center">
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Aprovadas</p>
                <p className="text-base font-semibold text-success-600 dark:text-success-400">{captured}</p>
              </div>
              <div className="text-center">
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Recusadas</p>
                <p className="text-base font-semibold text-error-600 dark:text-error-400">{declined}</p>
              </div>
              <div className="text-center">
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Conversão</p>
                <p className="text-base font-semibold text-brand-600 dark:text-brand-400">{conversion}%</p>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
