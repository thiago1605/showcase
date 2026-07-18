"use client";
import React, { useMemo } from "react";
import dynamic from "next/dynamic";
import Link from "next/link";
import type { ApexOptions } from "apexcharts";
import { useDashboardTimeseries } from "./useDashboardTimeseries";
import { useChartTheme } from "./useChartTheme";
import { Tooltip } from "@/components/ui/Tooltip";

const ReactApexChart = dynamic(() => import("react-apexcharts"), { ssr: false });

// ApexCharts pode chamar os formatters com undefined/null em alguns cenários
// (ponto missing, série vazia, hover fora dos dados). Sem o coerce, dispara
// "Cannot read properties of undefined (reading 'toLocaleString')" e mata o
// componente inteiro. Tratamos como 0 — formatter é só pra display.
function formatCurrency(value: number | undefined | null): string {
  const n = typeof value === "number" && Number.isFinite(value) ? value : 0;
  return n.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}
function formatCompact(value: number | undefined | null): string {
  const n = typeof value === "number" && Number.isFinite(value) ? value : 0;
  return n.toLocaleString("pt-BR", { notation: "compact", maximumFractionDigits: 1 });
}
// Backend serializa DashboardGranularity como int: 0=Day, 1=Week, 2=Hour.
// Aceitamos string também por robustez (caso o serializador mude).
function isHour(g: number | string): boolean {
  return g === 2 || g === "Hour";
}
function isWeek(g: number | string): boolean {
  return g === 1 || g === "Week";
}
function formatDate(iso: string, granularity: number | string): string {
  const d = new Date(iso);
  if (isHour(granularity)) {
    return d.toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit" });
  }
  return d.toLocaleDateString("pt-BR", {
    day: "2-digit",
    month: isWeek(granularity) ? "short" : "2-digit",
  });
}

export function VolumeTimeseriesChart() {
  const { data, loading, error } = useDashboardTimeseries();
  const chartTheme = useChartTheme();

  const {
    categories, volumeSeries, countSeries, countPlotSeries, totalVolume, totalCount, peak,
    avgPerPeriod, avgActiveDays, totalBuckets, activeBuckets, bucketLabel
  } = useMemo(() => {
    const empty = {
      categories: [], volumeSeries: [], countSeries: [], countPlotSeries: [] as Array<number | null>,
      totalVolume: 0, totalCount: 0,
      peak: null as null | { date: string; volume: number },
      avgPerPeriod: 0, avgActiveDays: 0, totalBuckets: 0, activeBuckets: 0,
      bucketLabel: "dia",
    };
    if (!data) return empty;

    const categories = data.points.map((p) => formatDate(p.date, data.granularity));
    const volumeSeries = data.points.map((p) => Number(p.volume.toFixed(2)));
    const countSeries = data.points.map((p) => p.count);
    const countPlotSeries = data.points.map((p) => (p.count > 0 ? p.count : null));
    const totalVolume = data.points.reduce((acc, p) => acc + p.volume, 0);
    const totalCount = data.points.reduce((acc, p) => acc + p.count, 0);

    // Duas métricas distintas pra evitar a armadilha de "média inflada":
    //   - avgPerPeriod: total ÷ N (buckets do período inteiro, incluindo dias zerados).
    //     É a média REAL — útil pra forecasting/planejamento.
    //   - avgActiveDays: total ÷ N (só buckets com movimento). É o "tamanho médio
    //     de um dia bom" — útil pra entender expectativa de pico.
    // Antes só mostrávamos avgActiveDays com label "Média / dia" — enganador
    // pq parecia ser média do período inteiro.
    const totalBuckets = data.points.length;
    const activeBuckets = data.points.filter((p) => p.volume > 0).length;
    const avgPerPeriod = totalBuckets > 0 ? totalVolume / totalBuckets : 0;
    const avgActiveDays = activeBuckets > 0 ? totalVolume / activeBuckets : 0;

    // Label do bucket adapta à granularidade — não "por dia" se for hora/semana.
    const bucketLabel = isHour(data.granularity) ? "hora" : isWeek(data.granularity) ? "semana" : "dia";

    const peakPt = data.points.reduce(
      (best, p) => (p.volume > (best?.volume ?? -Infinity) ? p : best),
      null as null | { date: string; volume: number }
    );
    const peak = peakPt && peakPt.volume > 0
      ? { date: formatDate(peakPt.date, data.granularity), volume: peakPt.volume }
      : null;

    return {
      categories, volumeSeries, countSeries, countPlotSeries, totalVolume, totalCount, peak,
      avgPerPeriod, avgActiveDays, totalBuckets, activeBuckets, bucketLabel,
    };
  }, [data]);

  const options: ApexOptions = useMemo(
    () => ({
      chart: {
        fontFamily: "Outfit, sans-serif",
        type: "area",
        height: 280,
        toolbar: { show: false },
        zoom: { enabled: false },
      },
      // Series 0 = Volume (área purple), Series 1 = Transações (linha/pontos cyan).
      // colors[i] = START do gradient (top-left). gradientToColors[i] = END
      // saturado (bottom-right).
      colors: ["#A855F7", "#67E8F9"], // purple-500, cyan-300 (light)
      dataLabels: { enabled: false },
      stroke: { curve: "smooth", width: [3, 3] },
      markers: {
        size: [0, 0],
        discrete: countSeries
          .map((count, index) =>
            count > 0
              ? {
                  seriesIndex: 1,
                  dataPointIndex: index,
                  fillColor: "#06B6D4",
                  strokeColor: chartTheme.isDark ? "#111827" : "#FFFFFF",
                  size: 5,
                  shape: "circle" as const,
                }
              : null
          )
          .filter((marker): marker is NonNullable<typeof marker> => marker !== null),
        hover: { sizeOffset: 2 },
      },
      fill: {
        type: ["gradient", "solid"],
        gradient: {
          // Área suave do volume; a série de transações é linha/ponto e não
          // precisa de preenchimento.
          type: "diagonal1",
          gradientToColors: ["#A855F7", "#06B6D4"],
          opacityFrom: [0.22, 1],
          opacityTo: [0.02, 1],
          stops: [0, 100],
        },
      },
      // Desativa hover lighten + crosshair vertical (mesma config dos outros
      // bar charts pra hover limpo, sem ghost shapes atrás da barra hovered).
      states: {
        hover: { filter: { type: "none" } },
        active: { filter: { type: "none" } },
      },
      xaxis: {
        categories,
        labels: {
          style: { colors: chartTheme.muted, fontSize: "11px" },
          rotate: 0,
          hideOverlappingLabels: true,
        },
        axisBorder: { show: false },
        axisTicks: { show: false },
        crosshairs: { show: false },
        tooltip: { enabled: false },
      },
      yaxis: [
        {
          labels: {
            formatter: (val: number) => `R$ ${formatCompact(val)}`,
            style: { colors: chartTheme.muted, fontSize: "11px" },
          },
        },
        {
          opposite: true,
          min: 0,
          max: (max: number) => Math.max(6, Math.ceil(max)),
          tickAmount: 6,
          labels: {
            // Defensive: ApexCharts pode passar undefined quando série está vazia.
            formatter: (val: number) =>
              typeof val === "number" && Number.isFinite(val) ? String(Math.round(val)) : "0",
            style: { colors: chartTheme.muted, fontSize: "11px" },
          },
        },
      ],
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
        intersect: false,
        theme: chartTheme.isDark ? "dark" : "light",
        y: [
          { formatter: (val: number) => formatCurrency(val) },
          {
            formatter: (val: number) =>
              typeof val === "number" && Number.isFinite(val) ? `${val} transações` : "0 transações",
          },
        ],
      },
      // Linha tracejada = média ATIVA (dos buckets com movimento). Posicionar
      // a linha em avgPerPeriod faria sentido conceitual mas visualmente fica
      // colada no eixo X em períodos com poucas vendas — perde valor visual.
      // A média ativa é referência mais útil pra leitura do gráfico ("uma
      // venda típica nesse período").
      annotations: avgActiveDays > 0
        ? {
            yaxis: [
              {
                y: Number(avgActiveDays.toFixed(2)),
                borderColor: chartTheme.muted,
                strokeDashArray: 4,
                opacity: 0.5,
              },
            ],
          }
        : undefined,
    }),
    [categories, countSeries, chartTheme, avgActiveDays]
  );

  if (loading) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full animate-pulse">
        <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
          <div className="h-4 w-40 bg-gray-200 dark:bg-gray-700 rounded" />
        </div>
        <div className="p-5">
          <div className="h-72 bg-gray-100 dark:bg-gray-800 rounded" />
        </div>
      </div>
    );
  }

  if (error) {
    // Error state com tom neutro (sem vermelho cru). Antes era texto error-600
    // em fundo branco — visualmente alarmante mesmo pra erros transientes
    // (rate limit, timeout). Agora segue o pattern de empty state: ícone
    // cinza + título + mensagem do backend. A severidade fica na mensagem,
    // não na cor — útil pra rate limit que NÃO é uma falha real.
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full flex flex-col">
        <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">
            Volume ao longo do período
          </h3>
        </div>
        <div className="p-8 flex-1 flex flex-col items-center justify-center text-center">
          <span className="inline-flex items-center justify-center w-12 h-12 rounded-full bg-gray-100 dark:bg-gray-800 mb-3 text-gray-400 dark:text-gray-500">
            <svg
              width="24"
              height="24"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              <circle cx="12" cy="12" r="10" />
              <line x1="12" y1="8" x2="12" y2="12" />
              <line x1="12" y1="16" x2="12.01" y2="16" />
            </svg>
          </span>
          <p className="text-sm font-medium text-gray-700 dark:text-gray-300">
            Não foi possível carregar o gráfico
          </p>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 max-w-xs">
            {error}
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full flex flex-col">
      <div className="flex flex-wrap items-center justify-between gap-3 px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <div>
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Volume ao longo do período</h3>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
            {data && isHour(data.granularity)
              ? "Agregado por hora"
              : data && isWeek(data.granularity)
                ? "Agregado semanal"
                : "Agregado diário"}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-4 text-right">
          <div>
            <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Total</p>
            <p className="text-sm font-semibold text-gray-900 dark:text-white tabular-nums">{formatCurrency(totalVolume)}</p>
          </div>
          <div>
            <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Transações</p>
            <p className="text-sm font-semibold text-gray-900 dark:text-white tabular-nums">{totalCount}</p>
          </div>
          {totalVolume > 0 && totalBuckets > 0 && (
            <div>
              {/* Média REAL do período (total ÷ todos os buckets, inclui dias
                  zerados). Tooltip mostra a "média ativa" pra contexto. */}
              <Tooltip
                side="bottom"
                maxWidth={260}
                content={
                  <div className="space-y-1.5 text-[11px]">
                    <p>
                      <span className="opacity-70">Período cheio:</span>{" "}
                      <span className="tabular-nums font-semibold">{formatCurrency(avgPerPeriod)}</span>{" "}
                      <span className="opacity-70">/ {bucketLabel}</span>
                    </p>
                    <p className="opacity-80">
                      Média sobre <span className="tabular-nums">{totalBuckets}</span> {bucketLabel}s,
                      {" "}<span className="tabular-nums">{activeBuckets}</span> com vendas.
                    </p>
                    {avgActiveDays > 0 && avgActiveDays !== avgPerPeriod && (
                      <p className="border-t border-white/20 pt-1.5">
                        <span className="opacity-70">Em {bucketLabel}s com venda:</span>{" "}
                        <span className="tabular-nums font-semibold">{formatCurrency(avgActiveDays)}</span>
                      </p>
                    )}
                  </div>
                }
              >
                <div className="cursor-help">
                  <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400 underline decoration-dotted">
                    Média / {bucketLabel}
                  </p>
                  <p className="text-sm font-semibold text-gray-900 dark:text-white tabular-nums">{formatCurrency(avgPerPeriod)}</p>
                </div>
              </Tooltip>
            </div>
          )}
          {peak && (
            <div>
              <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Pico em {peak.date}</p>
              <p className="text-sm font-semibold text-brand-600 dark:text-brand-400 tabular-nums">{formatCurrency(peak.volume)}</p>
            </div>
          )}
        </div>
      </div>
      <div className="p-5 flex-1 flex flex-col">
        {totalVolume === 0 && totalCount === 0 ? (
          // Empty state custom (em vez do EmptyStateCTA padrão) pra oferecer
          // 3 caminhos de ativação em vez de só um. Centralizado vertical
          // quando o card é esticado pra match a altura do Saldo ao lado.
          <div className="flex-1 flex items-center justify-center">
            <div className="text-center py-4 max-w-sm mx-auto">
              <p className="text-sm font-medium text-gray-700 dark:text-gray-300">
                Sem movimento no período
              </p>
              <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 mb-4">
                Comece sua jornada por um destes caminhos:
              </p>
              <div className="flex flex-col gap-2.5">
                <Link
                  href="/payment-links"
                  className="inline-flex items-center justify-center gap-1.5 text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400 dark:hover:text-brand-300 transition-colors"
                >
                  Criar link de pagamento
                  <svg width="10" height="10" viewBox="0 0 10 10" fill="none" aria-hidden="true">
                    <path d="M3 1l4 4-4 4" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                </Link>
                <Link
                  href="/products/new"
                  className="inline-flex items-center justify-center gap-1.5 text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400 dark:hover:text-brand-300 transition-colors"
                >
                  Criar produto
                  <svg width="10" height="10" viewBox="0 0 10 10" fill="none" aria-hidden="true">
                    <path d="M3 1l4 4-4 4" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                </Link>
                <Link
                  href="/affiliate-marketplace"
                  className="inline-flex items-center justify-center gap-1.5 text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400 dark:hover:text-brand-300 transition-colors"
                >
                  Afiliar-se a um produto
                  <svg width="10" height="10" viewBox="0 0 10 10" fill="none" aria-hidden="true">
                    <path d="M3 1l4 4-4 4" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                </Link>
              </div>
            </div>
          </div>
        ) : (
          <div role="img" aria-label={`Gráfico de volume e transações ao longo do período. Total: ${formatCurrency(totalVolume)} em ${totalCount} transações.`}>
            <ReactApexChart
              options={options}
              series={[
                { name: "Volume", type: "area", data: volumeSeries },
                { name: "Transações", type: "line", data: countPlotSeries },
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
