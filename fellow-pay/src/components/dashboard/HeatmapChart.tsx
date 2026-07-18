"use client";
import React, { useMemo } from "react";
import dynamic from "next/dynamic";
import type { ApexOptions } from "apexcharts";
import { useQuery } from "@tanstack/react-query";
import { dashboardService, HeatmapCell } from "@/services/dashboard.service";
import { useDashboardPeriod } from "./PeriodContext";
import { useChartTheme } from "./useChartTheme";
import { EmptyStateCTA } from "./EmptyStateCTA";

const ReactApexChart = dynamic(() => import("react-apexcharts"), { ssr: false });

const DAY_LABELS = ["Dom", "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb"];

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

interface ApexHeatmapPoint {
  x: string;
  y: number;
}

interface ApexSeries {
  name: string;
  data: ApexHeatmapPoint[];
}

function buildSeries(cells: HeatmapCell[]): ApexSeries[] {
  // Estrutura esperada pelo Apex: uma série por linha (dia da semana), pontos por hora.
  // Renderiza de baixo pra cima — então mantemos Sábado em cima invertendo a ordem.
  const grid: number[][] = Array.from({ length: 7 }, () => Array(24).fill(0));
  for (const c of cells) {
    if (c.dayOfWeek >= 0 && c.dayOfWeek < 7 && c.hour >= 0 && c.hour < 24) {
      grid[c.dayOfWeek][c.hour] = c.count;
    }
  }
  // Apex desenha a primeira série na linha de baixo. Pra ler "Dom em cima → Sáb embaixo"
  // tipo calendário, percorremos invertido.
  return [...DAY_LABELS]
    .map((label, dayIdx) => ({
      name: label,
      data: grid[dayIdx].map((count, hour) => ({ x: `${hour.toString().padStart(2, "0")}h`, y: count })),
    }))
    .reverse();
}

export function HeatmapChart() {
  const { period } = useDashboardPeriod();
  const chartTheme = useChartTheme();

  const { data, isLoading, error } = useQuery({
    queryKey: ["dashboard", "heatmap", period.from, period.to],
    queryFn: () => dashboardService.getHeatmap({ from: period.from, to: period.to }),
  });

  const { series, totalCount, peak } = useMemo(() => {
    if (!data) return { series: [] as ApexSeries[], totalCount: 0, peak: null as null | { dow: number; hour: number; count: number; volume: number } };
    const totalCount = data.cells.reduce((acc, c) => acc + c.count, 0);
    const peakCell = data.cells.reduce<HeatmapCell | null>((best, c) => (c.count > (best?.count ?? -1) ? c : best), null);
    return {
      series: buildSeries(data.cells),
      totalCount,
      peak: peakCell ? { dow: peakCell.dayOfWeek, hour: peakCell.hour, count: peakCell.count, volume: peakCell.volume } : null,
    };
  }, [data]);

  const options: ApexOptions = useMemo(
    () => ({
      chart: {
        type: "heatmap",
        fontFamily: "Outfit, sans-serif",
        toolbar: { show: false },
        background: "transparent",
      },
      theme: { mode: chartTheme.isDark ? "dark" : "light" },
      dataLabels: { enabled: false },
      colors: ["#b026ff"], // brand purple — Apex deriva ranges de intensidade automaticamente
      plotOptions: {
        heatmap: {
          shadeIntensity: 0.5,
          radius: 4,
          useFillColorAsStroke: false,
          colorScale: {
            ranges: [
              { from: 0, to: 0, color: chartTheme.isDark ? "#1F2937" : "#F3F4F6", name: "Sem movimento" },
              { from: 1, to: 2, color: "#E9E4FF", name: "Baixo" },
              { from: 3, to: 9, color: "#B8A9FF", name: "Médio" },
              { from: 10, to: 49, color: "#b026ff", name: "Alto" },
              { from: 50, to: 9999, color: "#4A32BF", name: "Muito alto" },
            ],
          },
        },
      },
      xaxis: {
        labels: { style: { colors: chartTheme.muted, fontSize: "10px" } },
        axisBorder: { show: false },
        axisTicks: { show: false },
      },
      yaxis: {
        labels: { style: { colors: chartTheme.muted, fontSize: "11px" } },
      },
      grid: { padding: { right: 20 } },
      tooltip: {
        theme: chartTheme.isDark ? "dark" : "light",
        y: { formatter: (val: number) => val === 1 ? `${val} transação capturada` : `${val} transações capturadas` },
      },
    }),
    [chartTheme]
  );

  if (isLoading) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 animate-pulse">
        <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
          <div className="h-4 w-48 bg-gray-200 dark:bg-gray-700 rounded" />
        </div>
        <div className="p-5">
          <div className="h-72 bg-gray-100 dark:bg-gray-800 rounded" />
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900">
        <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Heatmap dia × hora</h3>
        </div>
        <div className="p-5">
          <p className="text-sm text-error-600 dark:text-error-400">
            {error instanceof Error ? error.message : "Erro ao carregar heatmap."}
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900">
      <div className="flex flex-wrap items-center justify-between gap-3 px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <div>
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Quando seus clientes pagam</h3>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
            Transações capturadas por dia da semana e hora (UTC)
          </p>
        </div>
        {peak && (
          <div className="text-right">
            <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Pico</p>
            <p className="text-sm font-semibold text-brand-600 dark:text-brand-400">
              {DAY_LABELS[peak.dow]} às {peak.hour.toString().padStart(2, "0")}h — {peak.count} tx
            </p>
          </div>
        )}
      </div>
      <div className="p-5">
        {totalCount === 0 ? (
          <EmptyStateCTA
            title="Sem transações capturadas no período"
            description="O heatmap se preenche conforme você captura pagamentos. Comece sua jornada por um destes caminhos:"
            actions={[
              { label: "Criar link de pagamento", href: "/payment-links" },
              { label: "Criar produto", href: "/products/new" },
              { label: "Afiliar-se a um produto", href: "/affiliate-marketplace" },
            ]}
          />
        ) : (
          <div role="img" aria-label={`Heatmap de ${totalCount} transações capturadas distribuídas por dia da semana e hora.`}>
            <ReactApexChart options={options} series={series} type="heatmap" height={300} />
          </div>
        )}
      </div>
    </div>
  );
}
