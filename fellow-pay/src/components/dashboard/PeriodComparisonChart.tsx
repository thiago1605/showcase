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
function formatCompact(value: number): string {
  return value.toLocaleString("pt-BR", { notation: "compact", maximumFractionDigits: 1 });
}

/**
 * Sobrepõe a curva de volume do período atual com a do período anterior de mesma
 * duração. Eixo X usa "índice do dia/hora dentro do período" (não datas absolutas)
 * pra que duas séries de tamanhos iguais alinhem ponto a ponto.
 */
export function PeriodComparisonChart() {
  const { period } = useDashboardPeriod();
  const chartTheme = useChartTheme();

  // Forçamos a mesma granularidade pros 2 fetches — caso contrário, períodos com
  // configs diferentes (ex: TODAY=Hour vs prev=Day) gerariam séries com tamanhos
  // diferentes e o alinhamento por índice quebraria.
  const granularity = period.preset === "TODAY" ? "Hour" : undefined;

  const current = useQuery({
    queryKey: ["dashboard", "timeseries", period.from, period.to, period.preset, granularity],
    queryFn: () => dashboardService.getTimeseries({ from: period.from, to: period.to, granularity }),
  });

  const previous = useQuery({
    queryKey: ["dashboard", "timeseries-comparison", period.prevFrom, period.prevTo, period.preset, granularity],
    queryFn: () => dashboardService.getTimeseries({ from: period.prevFrom, to: period.prevTo, granularity }),
  });

  const isLoading = current.isLoading || previous.isLoading;
  const error = current.error ?? previous.error;

  const { categories, currentSeries, previousSeries, totalCurrent, totalPrevious, deltaPercent } = useMemo(() => {
    if (!current.data || !previous.data) {
      return { categories: [], currentSeries: [], previousSeries: [], totalCurrent: 0, totalPrevious: 0, deltaPercent: null as number | null };
    }
    // Alinhamento: usamos o menor comprimento — protege contra DST / mismatch de
    // pontos quando os dois períodos não tiverem o mesmo número de buckets.
    const len = Math.min(current.data.points.length, previous.data.points.length);
    // Label do bucket reflete a granularidade real devolvida pelo backend.
    // 0=Day, 1=Week, 2=Hour (alinhado com DashboardGranularity no backend).
    const g = current.data.granularity;
    const bucketLabel = g === 2 || g === "Hour" ? "Hora" : g === 1 || g === "Week" ? "Sem." : "Dia";
    const categories = Array.from({ length: len }, (_, i) => `${bucketLabel} ${i + 1}`);
    const currentSeries = current.data.points.slice(0, len).map((p) => Number(p.volume.toFixed(2)));
    const previousSeries = previous.data.points.slice(0, len).map((p) => Number(p.volume.toFixed(2)));
    const totalCurrent = currentSeries.reduce((acc, v) => acc + v, 0);
    const totalPrevious = previousSeries.reduce((acc, v) => acc + v, 0);
    const deltaPercent = totalPrevious > 0 ? ((totalCurrent - totalPrevious) / totalPrevious) * 100 : null;
    return { categories, currentSeries, previousSeries, totalCurrent, totalPrevious, deltaPercent };
  }, [current.data, previous.data]);

  const options: ApexOptions = useMemo(
    () => ({
      chart: {
        type: "line",
        fontFamily: "Outfit, sans-serif",
        toolbar: { show: false },
        zoom: { enabled: false },
        background: "transparent",
      },
      theme: { mode: chartTheme.isDark ? "dark" : "light" },
      // Brand indigo pra "período atual" + cinza pra "anterior" — emphasis.
      // Mesma cor primária usada pelo VolumeTimeseriesChart (#7B61FF), pra
      // consistência cross-chart no painel + insights.
      colors: ["#7B61FF", "#9CA3AF"],
      stroke: { curve: "smooth", width: [3, 2], dashArray: [0, 4] },
      dataLabels: { enabled: false },
      xaxis: {
        categories,
        labels: { style: { colors: chartTheme.muted, fontSize: "10px" }, hideOverlappingLabels: true },
        axisBorder: { show: false },
        axisTicks: { show: false },
      },
      yaxis: {
        labels: {
          formatter: (val: number) => `R$ ${formatCompact(val)}`,
          style: { colors: chartTheme.muted, fontSize: "11px" },
        },
      },
      legend: {
        position: "top",
        horizontalAlign: "right",
        fontSize: "12px",
        labels: { colors: chartTheme.muted },
        markers: { size: 6 },
      },
      grid: {
        borderColor: chartTheme.grid,
        strokeDashArray: 3,
        yaxis: { lines: { show: true } },
        xaxis: { lines: { show: false } },
      },
      tooltip: {
        shared: true,
        theme: chartTheme.isDark ? "dark" : "light",
        y: { formatter: (val: number) => formatCurrency(val) },
      },
    }),
    [categories, chartTheme]
  );

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900">
      <div className="flex flex-wrap items-center justify-between gap-3 px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <div>
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Período atual vs anterior</h3>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
            Curvas alinhadas pelo índice do bucket (não pela data absoluta)
          </p>
        </div>
        {!isLoading && totalCurrent + totalPrevious > 0 && (
          <div className="flex flex-wrap items-center gap-4 text-right">
            <div>
              <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Atual</p>
              <p className="text-sm font-semibold text-brand-600 dark:text-brand-400">{formatCurrency(totalCurrent)}</p>
            </div>
            <div>
              <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Anterior</p>
              <p className="text-sm font-semibold text-gray-600 dark:text-gray-400">{formatCurrency(totalPrevious)}</p>
            </div>
            {deltaPercent !== null && Number.isFinite(deltaPercent) && (
              <div>
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Variação</p>
                <p className={`text-sm font-semibold ${deltaPercent >= 0 ? "text-success-600 dark:text-success-400" : "text-error-600 dark:text-error-400"}`}>
                  {deltaPercent >= 0 ? "+" : ""}
                  {deltaPercent.toFixed(1)}%
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
            {error instanceof Error ? error.message : "Erro ao carregar comparação."}
          </p>
        )}

        {!isLoading && !error && totalCurrent === 0 && totalPrevious === 0 && (
          <EmptyStateCTA
            title="Sem volume nos dois períodos"
            description="A comparação aparece quando houver movimento no período atual e/ou no anterior."
          />
        )}

        {!isLoading && !error && (totalCurrent > 0 || totalPrevious > 0) && (
          <div role="img" aria-label={`Comparação de volume entre período atual (${formatCurrency(totalCurrent)}) e anterior (${formatCurrency(totalPrevious)}).`}>
            <ReactApexChart
              options={options}
              series={[
                { name: "Período atual", data: currentSeries },
                { name: "Período anterior", data: previousSeries },
              ]}
              type="line"
              height={280}
            />
          </div>
        )}
      </div>
    </div>
  );
}
