"use client";
import React, { useMemo } from "react";
import dynamic from "next/dynamic";
import type { ApexOptions } from "apexcharts";
import { useQuery } from "@tanstack/react-query";
import { dashboardService } from "@/services/dashboard.service";
import { useDashboardPeriod } from "./PeriodContext";
import { useChartTheme } from "./useChartTheme";
import { EmptyStateCTA } from "./EmptyStateCTA";

const ReactApexChart = dynamic(() => import("react-apexcharts"), { ssr: false });

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

export function TicketDistributionChart() {
  const { period } = useDashboardPeriod();
  const chartTheme = useChartTheme();

  const { data, isLoading, error } = useQuery({
    queryKey: ["dashboard", "ticket-distribution", period.from, period.to],
    queryFn: () => dashboardService.getTicketDistribution({ from: period.from, to: period.to }),
  });

  const { categories, counts, modalLabel } = useMemo(() => {
    if (!data) return { categories: [], counts: [], modalLabel: null as string | null };
    const categories = data.bins.map((b) => b.label);
    const counts = data.bins.map((b) => b.count);
    // Modal = bin com mais transações (faixa "típica" do seller).
    const modal = data.bins.reduce<typeof data.bins[number] | null>((best, b) => (b.count > (best?.count ?? -1) ? b : best), null);
    return { categories, counts, modalLabel: modal && modal.count > 0 ? modal.label : null };
  }, [data]);

  const options: ApexOptions = useMemo(
    () => ({
      chart: {
        type: "bar",
        fontFamily: "Outfit, sans-serif",
        toolbar: { show: false },
        background: "transparent",
        // Re-mede a altura quando o parent flex resolve depois do mount.
        // Sem isso, com height="100%" o Apex pega o min-height (260) na
        // primeira render e nunca re-renderiza — deixa um gap visível
        // que só some no F5 (quando o parent já tem altura final).
        redrawOnParentResize: true,
      },
      theme: { mode: chartTheme.isDark ? "dark" : "light" },
      // Cores espelhando o pill-gradient-brand do header (#b07ae0 → #7029bd
      // em 135deg, definidas em globals.css). Apex: colors[0] = start (light,
      // top-left), gradientToColors[0] = end (dark, bottom-right).
      colors: ["#b07ae0"],
      // borderRadius 14: pill consistente em todas as alturas, sem deformar
      // os bins menores (R$ 200–500 ou R$ 5k+ com 1-2 transações).
      plotOptions: {
        bar: {
          horizontal: false,
          columnWidth: "55%",
          borderRadius: 14,
          borderRadiusApplication: "around",
        },
      },
      dataLabels: { enabled: false },
      stroke: { width: 0 },
      // Gradient diagonal2 = flui do canto top-right ao bottom-left.
      // opacityFrom 0.55 (start = top-right = highlight glossy levemente
      // translúcido) → opacityTo 1 (end = bottom-left = saturado).
      // shadeIntensity 0.6 mistura com uma versão light da cor base,
      // criando o "shine" sem desaturar o resto.
      // Gradient diagonal1 = 135deg (start top-left → end bottom-right),
      // mesma direção dos pills. Sem opacity tricks pra manter cores intensas.
      fill: {
        type: "gradient",
        gradient: {
          type: "diagonal1",
          gradientToColors: ["#8B47D9"], // um shade mais claro que #7029bd
          opacityFrom: 1,
          opacityTo: 1,
          stops: [0, 100],
        },
      },
      xaxis: {
        categories,
        labels: { style: { colors: chartTheme.muted, fontSize: "11px" } },
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
        labels: { style: { colors: chartTheme.muted, fontSize: "11px" }, formatter: (v: number) => String(Math.round(v)) },
      },
      grid: {
        borderColor: chartTheme.grid,
        strokeDashArray: 3,
        yaxis: { lines: { show: true } },
        xaxis: { lines: { show: false } },
      },
      tooltip: {
        theme: chartTheme.isDark ? "dark" : "light",
        // marker do tooltip usa o purple saturado (gradientToColor), casando
        // com o tom da barra ao invés do roxo pastel #b07ae0 do `colors`.
        marker: { fillColors: ["#8B47D9"] },
        y: { formatter: (val: number) => `${val} ${val === 1 ? "transação" : "transações"}` },
      },
    }),
    [categories, chartTheme]
  );

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full flex flex-col">
      <div className="flex flex-wrap items-center justify-between gap-3 px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <div>
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Distribuição de tickets</h3>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
            Faixas de valor das capturas (apenas concluídas)
          </p>
        </div>
        {data && data.totalCount > 0 && (
          <div className="flex items-center gap-4 text-right">
            <div>
              <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Médio</p>
              <p className="text-sm font-semibold text-gray-900 dark:text-white">{formatCurrency(data.averageTicket)}</p>
            </div>
            <div>
              <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Mediano</p>
              <p className="text-sm font-semibold text-gray-900 dark:text-white">{formatCurrency(data.medianTicket)}</p>
            </div>
            {modalLabel && (
              <div>
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Faixa típica</p>
                <p className="text-sm font-semibold text-brand-600 dark:text-brand-400">{modalLabel}</p>
              </div>
            )}
          </div>
        )}
      </div>
      <div className="p-5 flex-1 flex flex-col">
        {isLoading && <div className="flex-1 min-h-[200px] bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />}

        {error && !isLoading && (
          <p className="text-sm text-error-600 dark:text-error-400">
            {error instanceof Error ? error.message : "Erro ao carregar."}
          </p>
        )}

        {!isLoading && !error && data && data.totalCount === 0 && (
          <div className="flex-1 flex items-center justify-center">
            <EmptyStateCTA
              title="Sem capturas no período"
              description="A distribuição se preenche assim que houver transações capturadas."
            />
          </div>
        )}

        {!isLoading && !error && data && data.totalCount > 0 && (
          <div
            role="img"
            aria-label={`Histograma de ${data.totalCount} transações capturadas distribuídas em ${data.bins.length} faixas de valor.`}
            className="flex-1 min-h-[260px]"
          >
            <ReactApexChart options={options} series={[{ name: "Transações", data: counts }]} type="bar" height="100%" />
          </div>
        )}
      </div>
    </div>
  );
}
