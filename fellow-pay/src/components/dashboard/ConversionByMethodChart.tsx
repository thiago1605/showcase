"use client";
import React, { useMemo } from "react";
import dynamic from "next/dynamic";
import type { ApexOptions } from "apexcharts";
import { useQuery } from "@tanstack/react-query";
import { dashboardService } from "@/services/dashboard.service";
import { useDashboardPeriod } from "./PeriodContext";
import { useChartTheme } from "./useChartTheme";
import { paymentTypeLabel } from "@/lib/formatters/enums";
import { EmptyStateCTA } from "./EmptyStateCTA";

const ReactApexChart = dynamic(() => import("react-apexcharts"), { ssr: false });

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

/**
 * Bar chart vertical stacked com a mesma estética dos outros bar charts do
 * app (pill-shaped + gradient duotone). Uma coluna por método de pagamento;
 * cada coluna tem 3 segmentos empilhados — capturadas (success/green),
 * pendentes (warning/amber) e recusadas (error/red). Mantém a semântica
 * de cor do design anterior (HTML stacked progress) mas alinha o look ao
 * resto do app, e ainda permite comparação cross-método via altura total.
 */
export function ConversionByMethodChart() {
  const { period } = useDashboardPeriod();
  const chartTheme = useChartTheme();

  const { data, isLoading, error } = useQuery({
    queryKey: ["dashboard", "conversion", period.from, period.to],
    queryFn: () => dashboardService.getConversionByMethod({ from: period.from, to: period.to }),
  });

  const items = data ?? [];
  const totalAll = items.reduce((acc, i) => acc + i.total, 0);

  const { categories, captured, pending, declined, methodStats, bestMethod, worstMethod } = useMemo(() => {
    if (items.length === 0) {
      return { categories: [], captured: [], pending: [], declined: [], methodStats: [], bestMethod: null, worstMethod: null };
    }
    // Ordena por total desc — método mais movimentado à esquerda (mais peso visual).
    const sorted = [...items].sort((a, b) => b.total - a.total);
    // Best/worst de aprovação — só conta métodos com volume mínimo (>1 tx) pra
    // não destacar boletos solitários com 100% ou 0%.
    const eligible = sorted.filter((m) => m.total >= 2);
    const best = eligible.length > 0
      ? eligible.reduce((b, m) => (m.approvalRate > b.approvalRate ? m : b), eligible[0])
      : null;
    const worst = eligible.length > 0
      ? eligible.reduce((w, m) => (m.approvalRate < w.approvalRate ? m : w), eligible[0])
      : null;
    return {
      categories: sorted.map((i) => paymentTypeLabel(i.paymentType)),
      captured: sorted.map((i) => i.captured),
      pending: sorted.map((i) => i.pending),
      declined: sorted.map((i) => i.declined),
      methodStats: sorted,
      bestMethod: best,
      worstMethod: worst,
    };
  }, [items]);

  const options: ApexOptions = useMemo(
    () => ({
      chart: {
        type: "bar",
        stacked: true,
        fontFamily: "Outfit, sans-serif",
        toolbar: { show: false },
        background: "transparent",
      },
      theme: { mode: chartTheme.isDark ? "dark" : "light" },
      // Light starts (Tailwind 300) — gradient pill light → dark, mesmo
      // padrão das outras barras (TicketDistribution, PaymentMethodChart,
      // TransactionsByStatus).
      colors: ["#86EFAC", "#FCD34D", "#FCA5A5"],
      // HORIZONTAL stacked: cada método vira uma "linha" — total horizontal
      // dividido em 3 segmentos coloridos (captura/pendente/recusada).
      // Em horizontal stacked o gradient vertical funciona melhor que em
      // vertical stacked porque os segmentos ficam lado-a-lado (não
      // sobrepostos), evitando o look listrado.
      plotOptions: {
        bar: {
          horizontal: true,
          barHeight: "55%",
          borderRadius: 10,
          borderRadiusApplication: "end",
          borderRadiusWhenStacked: "last",
        },
      },
      dataLabels: { enabled: false },
      stroke: { width: 0 },
      // Pill-style gradient: light start → dark end, mesmo padrão dos
      // outros bar charts. Por série: Capturadas verde, Pendentes amber,
      // Recusadas red — todas indo de 300 (light) a 700 (dark saturado).
      fill: {
        type: "gradient",
        gradient: {
          type: "diagonal1",
          gradientToColors: ["#22C55E", "#F59E0B", "#EF4444"], // green-500, amber-500, red-500
          opacityFrom: 1,
          opacityTo: 1,
          stops: [0, 100],
        },
      },
      xaxis: {
        categories,
        // Em horizontal, o xaxis vira o eixo NUMÉRICO (counts). Formatter
        // arredonda pra inteiro (counts não são decimais).
        labels: {
          style: { colors: chartTheme.muted, fontSize: "11px" },
          formatter: (v: string) => String(Math.round(Number(v))),
        },
        axisBorder: { show: false },
        axisTicks: { show: false },
        // Sem faixa fantasma horizontal/vertical no hover (ver TransactionsByStatus).
        crosshairs: { show: false },
        tooltip: { enabled: false },
      },
      states: {
        hover: { filter: { type: "none" } },
        active: { filter: { type: "none" } },
      },
      yaxis: {
        // Em horizontal, yaxis recebe os NOMES dos métodos (categóricos).
        // Sem formatter (estava fazendo Math.round em "Pix" → NaN).
        labels: { style: { colors: chartTheme.muted, fontSize: "12px" } },
      },
      legend: {
        show: true,
        position: "bottom",
        horizontalAlign: "center",
        fontSize: "12px",
        labels: { colors: chartTheme.muted },
        // fillColors = darks (gradientToColors). Sem isso o marker da legenda
        // usa `colors[seriesIndex]` (start pastel do gradient) e fica pálido
        // vs. a barra que mostra principalmente o tom saturado.
        markers: {
          size: 6,
          shape: "circle",
          fillColors: ["#22C55E", "#F59E0B", "#EF4444"],
        },
        itemMargin: { horizontal: 10, vertical: 4 },
      },
      grid: {
        borderColor: chartTheme.grid,
        strokeDashArray: 3,
        yaxis: { lines: { show: true } },
        xaxis: { lines: { show: false } },
      },
      tooltip: {
        theme: chartTheme.isDark ? "dark" : "light",
        shared: true,
        intersect: false,
        // marker = darks (gradientToColors do fill). Sem isso o marker usa
        // os tons pastel #86EFAC/#FCD34D/#FCA5A5 do `colors[]` (start do
        // gradient), pálidos demais comparado à barra saturada.
        marker: { fillColors: ["#22C55E", "#F59E0B", "#EF4444"] },
        // Tooltip default mostra count por série (já útil). O detalhe extra
        // (taxa de aprovação + volume capturado) vai no formatter da série
        // "Capturadas" pra ficar próximo do contexto visual.
        y: {
          formatter: (val: number, opts: { seriesIndex: number; dataPointIndex: number }) => {
            const m = methodStats[opts.dataPointIndex];
            if (!m) return `${val} ${val === 1 ? "transação" : "transações"}`;
            if (opts.seriesIndex === 0) {
              // Capturadas: count + volume + approval rate
              return `${val} · ${formatCurrency(m.capturedVolume)} (${m.approvalRate.toFixed(1)}% aprovação)`;
            }
            return `${val} ${val === 1 ? "transação" : "transações"}`;
          },
        },
      },
    }),
    [categories, chartTheme, methodStats]
  );

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900">
      <div className="flex flex-wrap items-center justify-between gap-3 px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <div>
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Conversão por método</h3>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
            Capturadas, pendentes e recusadas — quem está vazando mais
          </p>
        </div>
        {/* Resumo no header: melhor e pior aprovação (entre métodos com >=2 tx).
            Substitui o display inline de approval rate que o design anterior
            tinha por método, mantendo o sinal acionável visível sem hover. */}
        {(bestMethod || worstMethod) && (
          <div className="flex items-center gap-4 text-right">
            {bestMethod && (
              <div>
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Melhor</p>
                <p className="text-sm font-semibold text-success-600 dark:text-success-400">
                  {paymentTypeLabel(bestMethod.paymentType)} · {bestMethod.approvalRate.toFixed(1)}%
                </p>
              </div>
            )}
            {worstMethod && worstMethod !== bestMethod && (
              <div>
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Pior</p>
                <p className="text-sm font-semibold text-error-600 dark:text-error-400">
                  {paymentTypeLabel(worstMethod.paymentType)} · {worstMethod.approvalRate.toFixed(1)}%
                </p>
              </div>
            )}
          </div>
        )}
      </div>
      <div className="p-5">
        {isLoading && <div className="h-72 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />}

        {error && !isLoading && (
          <p className="text-sm text-error-600 dark:text-error-400">
            {error instanceof Error ? error.message : "Erro ao carregar."}
          </p>
        )}

        {!isLoading && !error && totalAll === 0 && (
          <EmptyStateCTA
            title="Sem transações no período"
            description="Quando houver volume, esta seção mostra qual método está vazando mais."
          />
        )}

        {!isLoading && !error && totalAll > 0 && (
          <div role="img" aria-label={`Distribuição de status (capturadas, pendentes, recusadas) por método de pagamento.`}>
            <ReactApexChart
              options={options}
              series={[
                { name: "Capturadas", data: captured },
                { name: "Pendentes", data: pending },
                { name: "Recusadas", data: declined },
              ]}
              type="bar"
              height={280}
            />
          </div>
        )}
      </div>
    </div>
  );
}
