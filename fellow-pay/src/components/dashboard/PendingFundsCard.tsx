"use client";

import React, { useMemo } from "react";
import dynamic from "next/dynamic";
import { useRouter } from "next/navigation";
import type { ApexOptions } from "apexcharts";
import { useDashboardSummary } from "./useDashboardSummary";
import { useDashboardPeriod, toLocalDateString } from "./PeriodContext";
import { useChartTheme } from "./useChartTheme";
import { transactionStatusKey, paymentTypeKey, paymentTypeLabel } from "@/lib/formatters/enums";

const ReactApexChart = dynamic(() => import("react-apexcharts"), { ssr: false });

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

// Pendentes: ainda podem virar capturadas (esperando pagador/autorização).
// Não convertidas: terminais negativas — não vão mais virar receita.
const PENDING_STATUSES = new Set(["CREATED", "PROCESSING", "AUTHORIZED"]);
const UNCONVERTED_STATUSES = new Set(["DECLINED", "VOIDED", "FAILED", "CHARGEBACKERROR"]);

// Métodos em que "aguardando pagamento" faz sentido operacional — o cliente
// precisa agir DEPOIS de iniciar a transação (escanear QR, ir ao banco).
// Cartão NÃO entra: capture imediato significa que CREATED/PROCESSING duram
// segundos; AUTHORIZED só aparece em fluxo auth+capture diferido (raro em
// e-commerce padrão BR). Cartão em estado transitório é noise/bug de
// integração, não comportamento esperado de pagador — tratado em indicador
// discreto separado.
const AWAITING_PAYMENT_METHODS = new Set(["PIX", "BOLETO"]);

interface MethodAgg {
  key: string;
  label: string;
  pendingCount: number;
  pendingVolume: number;
  unconvertedCount: number;
  unconvertedVolume: number;
}

/**
 * Bar chart agrupado estilo Dokue ("Statistic" da referência). Mostra
 * pendentes vs não convertidas lado a lado por método de pagamento. Sem
 * container de card — só o conteúdo flutuando direto no fundo do dashboard,
 * conforme o look-and-feel Dokue solicitado.
 *
 * Cores:
 *   - Pendentes → amber (warning)
 *   - Não convertidas → red (error)
 *
 * Cada barra é pill-shaped com gradient vertical duotone. Tooltip mostra
 * detalhe (count + volume) por bucket. Clique no chart navega para
 * /transactions filtrado pelo respectivo bucket.
 */
export function PendingFundsCard() {
  const { current, loading } = useDashboardSummary();
  const { period } = useDashboardPeriod();
  const router = useRouter();
  const chartTheme = useChartTheme();

  // Agrega aguardando pagamento + não convertidas por método em uma única
  // passada. Cartão em estado transitório (CREATED/PROCESSING/AUTHORIZED)
  // sai pra `cardTransient` — vira indicador discreto no rodapé.
  const { byMethod, pendingTotal, unconvertedTotal, cardTransient, allEmpty } = useMemo(() => {
    const map = new Map<string, MethodAgg>();
    let pendingTotal = { count: 0, volume: 0 };
    let unconvertedTotal = { count: 0, volume: 0 };
    let cardTransient = { count: 0, volume: 0 };

    if (!current) {
      return {
        byMethod: [] as MethodAgg[],
        pendingTotal,
        unconvertedTotal,
        cardTransient,
        allEmpty: true,
      };
    }

    for (const row of current.byStatusAndMethod ?? []) {
      const statusKey = transactionStatusKey(row.status) ?? "";
      const methodKey = paymentTypeKey(row.paymentType) ?? String(row.paymentType);
      const isPending = PENDING_STATUSES.has(statusKey);
      const isUnconverted = UNCONVERTED_STATUSES.has(statusKey);

      // Cartão em estado pendente NÃO entra no bucket "Aguardando pagamento"
      // — cartão deveria capturar em segundos. Se está parado aqui, é stuck
      // job ou auth+capture diferido (raro). Vai pro indicador "processamento
      // prolongado" pra sinalizar potencial problema sem inflar a métrica.
      if (isPending && !AWAITING_PAYMENT_METHODS.has(methodKey)) {
        cardTransient = {
          count: cardTransient.count + row.count,
          volume: cardTransient.volume + row.volume,
        };
        continue;
      }

      if (!isPending && !isUnconverted) continue;

      const existing = map.get(methodKey);
      const agg: MethodAgg = existing ?? {
        key: methodKey,
        label: paymentTypeLabel(row.paymentType),
        pendingCount: 0,
        pendingVolume: 0,
        unconvertedCount: 0,
        unconvertedVolume: 0,
      };
      if (isPending) {
        agg.pendingCount += row.count;
        agg.pendingVolume += row.volume;
        pendingTotal = {
          count: pendingTotal.count + row.count,
          volume: pendingTotal.volume + row.volume,
        };
      } else {
        agg.unconvertedCount += row.count;
        agg.unconvertedVolume += row.volume;
        unconvertedTotal = {
          count: unconvertedTotal.count + row.count,
          volume: unconvertedTotal.volume + row.volume,
        };
      }
      map.set(methodKey, agg);
    }

    // Ordena por volume total desc (mais "pesado" à esquerda).
    const byMethod = [...map.values()].sort(
      (a, b) =>
        b.pendingVolume + b.unconvertedVolume - (a.pendingVolume + a.unconvertedVolume),
    );

    return {
      byMethod,
      pendingTotal,
      unconvertedTotal,
      cardTransient,
      allEmpty: byMethod.length === 0 && cardTransient.count === 0,
    };
  }, [current]);

  const totalCount = pendingTotal.count + unconvertedTotal.count;
  const totalVolume = pendingTotal.volume + unconvertedTotal.volume;
  // Taxa de não conversão (% das transações que NÃO foram capturadas).
  const lossRate = useMemo(() => {
    const captured = current?.capturedCount ?? 0;
    const denominator = captured + totalCount;
    if (denominator === 0) return 0;
    return Math.round((unconvertedTotal.count / denominator) * 100);
  }, [current, totalCount, unconvertedTotal.count]);

  // Auto-oculta a série que está em 0. É comum num período curto ter só
  // aguardando pagamento OU só perdas — mostrar a série vazia polui legend +
  // dataLabels e desbalanceia visual. Aguardando = azul neutro (status, não
  // alarme); Não convertidas = vermelho (estado terminal negativo).
  const activeSeries = useMemo(() => {
    const list: Array<{ name: string; data: number[]; color: string; statusKey: string }> = [];
    if (pendingTotal.volume > 0) {
      list.push({
        name: "Aguardando pagamento",
        data: byMethod.map((m) => m.pendingVolume),
        color: "#3B82F6",
        statusKey: "PROCESSING",
      });
    }
    if (unconvertedTotal.volume > 0) {
      list.push({
        name: "Não convertidas",
        data: byMethod.map((m) => m.unconvertedVolume),
        color: "#ef4444",
        statusKey: "FAILED",
      });
    }
    return list;
  }, [byMethod, pendingTotal.volume, unconvertedTotal.volume]);

  // Drilldown ao clicar numa barra. Resolve o status via activeSeries (não
  // por índice fixo) — quando uma série está oculta, o seriesIndex 0 deixa
  // de ser garantidamente "pending".
  const handleBarClick = (seriesIndex: number) => {
    const entry = activeSeries[seriesIndex];
    if (!entry) return;
    const fromDate = toLocalDateString(period.from);
    const toDate = toLocalDateString(period.to);
    router.push(
      `/transactions?status=${encodeURIComponent(entry.statusKey)}&from=${fromDate}&to=${toDate}`,
    );
  };

  const options: ApexOptions = useMemo(
    () => ({
      chart: {
        fontFamily: "Outfit, sans-serif",
        type: "bar",
        toolbar: { show: false },
        zoom: { enabled: false },
        events: {
          dataPointSelection: (
            _e: unknown,
            _ctx: unknown,
            config: { seriesIndex: number },
          ) => handleBarClick(config.seriesIndex),
        },
      },
      // Cores derivadas de activeSeries — pendentes (azul neutro) e não
      // convertidas (vermelho). Filtradas pra ocultar a série em 0.
      colors: activeSeries.map((s) => s.color),
      xaxis: {
        categories: byMethod.map((m) => m.label),
        // Eixo X (valores) escondido — tooltip dá o número exato. Limpa
        // visualmente, focando só nas barras + labels de método.
        labels: { show: false },
        axisBorder: { show: false },
        axisTicks: { show: false },
      },
      yaxis: {
        labels: {
          style: { colors: chartTheme.muted, fontSize: "12px" },
        },
        axisBorder: { show: false },
        axisTicks: { show: false },
      },
      legend: {
        show: true,
        position: "bottom",
        horizontalAlign: "center",
        fontSize: "12px",
        labels: { colors: chartTheme.muted },
        markers: { size: 6, shape: "circle" },
        itemMargin: { horizontal: 10, vertical: 4 },
      },
      // Mostra o valor (R$ arredondado) na ponta direita de cada barra —
      // leitura instantânea sem precisar hover. textAnchor "start" +
      // offsetX positivo posiciona o texto LOGO APÓS o tip da barra.
      dataLabels: {
        enabled: true,
        textAnchor: "start",
        offsetX: 8,
        formatter: (val: string | number | number[]) => {
          const n = typeof val === "number" ? val : Number(val);
          return Number.isFinite(n) ? `R$ ${Math.round(n).toLocaleString("pt-BR")}` : "";
        },
        style: {
          colors: [chartTheme.muted],
          fontSize: "11px",
          fontWeight: 500,
        },
        // Sem sombra/contorno — texto pura cor muted (chip-style leve).
        dropShadow: { enabled: false },
      },
      stroke: { width: 0 },
      // Barras HORIZONTAIS pill-shaped finas (38% pra ficar elegante, não
      // gordas como referência da segunda print), agrupadas.
      plotOptions: {
        bar: {
          horizontal: true,
          barHeight: "38%",
          borderRadius: 8,
          borderRadiusApplication: "around",
          distributed: false,
        },
      },
      // Gradient horizontal sólido — sem fade. opacityTo: 0.95 evita o
      // efeito de "barras evaporando" da versão anterior (light/0.5).
      fill: {
        type: "gradient",
        gradient: {
          shade: "dark",
          type: "horizontal",
          shadeIntensity: 0.25,
          opacityFrom: 1,
          opacityTo: 0.95,
          stops: [0, 100],
        },
      },
      grid: { show: false, padding: { top: 0, right: 8, bottom: 0, left: 8 } },
      tooltip: {
        theme: chartTheme.isDark ? "dark" : "light",
        shared: false,
        intersect: true,
        // Tooltip mostra valor (R$) + contagem do método/bucket. Lookup do
        // count via activeSeries.name (não seriesIndex fixo) pra funcionar
        // mesmo quando uma das séries está oculta.
        y: {
          formatter: (val: number, opts: { seriesIndex: number; dataPointIndex: number }) => {
            const agg = byMethod[opts.dataPointIndex];
            if (!agg) return formatCurrency(val);
            const isPending = activeSeries[opts.seriesIndex]?.name === "Aguardando pagamento";
            const count = isPending ? agg.pendingCount : agg.unconvertedCount;
            return `${formatCurrency(val)} · ${count} transaç${count === 1 ? "ão" : "ões"}`;
          },
        },
      },
    }),
    // eslint-disable-next-line react-hooks/exhaustive-deps -- handleBarClick capturado
    [byMethod, chartTheme, activeSeries],
  );

  // Usa VOLUME (R$) — não count — pra que métodos com 1-2 transações de ticket
  // alto não fiquem "invisíveis" contra um método com 12 transações de ticket
  // baixo. Em count puro o boleto único (R$ 1.000) virava barra de 1px ao lado
  // do pix (12 unidades). Em volume, fica proporcional ao impacto financeiro.
  // Deriva de activeSeries — séries em 0 já foram filtradas lá em cima.
  const series = useMemo(
    () => activeSeries.map(({ name, data }) => ({ name, data })),
    [activeSeries],
  );

  if (loading || !current) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 p-5 space-y-3 animate-pulse">
        <div className="h-4 w-40 bg-gray-200/60 dark:bg-gray-800/60 rounded" />
        <div className="h-8 w-32 bg-gray-200/60 dark:bg-gray-800/60 rounded" />
        <div className="h-48 bg-gray-100/60 dark:bg-gray-800/40 rounded mt-3" />
      </div>
    );
  }

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 p-5">
      {/* Header Dokue-style: título + subtítulo discreto + valor grande +
          delta tipo "loss rate". */}
      <div className="flex items-start justify-between flex-wrap gap-3">
        <div>
          <h3 className="text-base font-semibold text-gray-900 dark:text-white">
            Receita em risco
          </h3>
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
            Volume não capturado por método, separado por status
          </p>
        </div>
      </div>

      <div className="mt-4 flex items-baseline gap-3 flex-wrap">
        <p className="text-3xl font-bold text-gray-900 dark:text-white tabular-nums tracking-tight leading-none">
          {totalCount}
        </p>
        <p className="text-sm text-gray-500 dark:text-gray-400">
          transaç{totalCount === 1 ? "ão" : "ões"} fora de receita
        </p>
        {lossRate > 0 && (
          <span className="inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-xs font-semibold pill-gradient-error text-white">
            {lossRate}% perda
          </span>
        )}
      </div>

      <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 tabular-nums">
        Volume total: <span className="font-semibold text-gray-700 dark:text-gray-300">{formatCurrency(totalVolume)}</span>
      </p>

      {/* Bar chart — só renderiza quando há dados. Empty state semantic abaixo.
          Altura adaptativa: cresce com o número de métodos (~56px/método +
          60px pra legend), com piso de 180px pra evitar chart "achatado"
          quando há só 1 método. */}
      {!allEmpty ? (
        <div className="mt-4 -mx-2">
          <ReactApexChart
            options={options}
            series={series}
            type="bar"
            height={Math.max(180, byMethod.length * 56 + 60)}
          />
        </div>
      ) : (
        <div className="mt-6 inline-flex items-center gap-2 text-sm text-gray-500 dark:text-gray-400">
          <svg
            width="16"
            height="16"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            className="text-success-500"
            aria-hidden="true"
          >
            <polyline points="20 6 9 17 4 12" />
          </svg>
          Nenhuma transação pendente ou perdida no período — taxa de conversão 100%.
        </div>
      )}

      {/* Indicador discreto pra cartões em estado transitório (CREATED/
          PROCESSING/AUTHORIZED). Cartão de crédito deveria capturar em
          segundos — se está parado, é sinal de stuck job ou auth+capture
          diferido. Não infla "Aguardando pagamento" mas o seller precisa
          saber. CTA leva pra /transactions filtrado pelo método pra
          investigação detalhada. */}
      {cardTransient.count > 0 && (
        <div className="mt-4 pt-3 border-t border-gray-100 dark:border-gray-800/60">
          <p className="inline-flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400">
            <svg
              width="14"
              height="14"
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
            {cardTransient.count} cart{cardTransient.count === 1 ? "ão" : "ões"} em
            processamento prolongado
            {" — "}
            <a
              href={`/transactions?paymentType=CREDIT_CARD&from=${toLocalDateString(period.from)}&to=${toLocalDateString(period.to)}`}
              className="font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400 underline-offset-2 hover:underline"
            >
              verificar
            </a>
          </p>
        </div>
      )}
    </div>
  );
}
