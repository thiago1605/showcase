"use client";
import React from "react";
import { useDashboardSummary } from "./useDashboardSummary";
import { useDashboardTimeseries } from "./useDashboardTimeseries";
import type { SellerDashboardSummary } from "@/services/dashboard.service";
import type { DashboardTimeseriesPoint } from "@/services/dashboard.service";

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

function formatPercent(value: number): string {
  return `${value.toFixed(1)}%`;
}

type MetricTone = "brand" | "success" | "blue" | "amber";

interface MetricDef {
  label: string;
  value: string;
  /** Linha secundária opcional (ex: "R$ 22.000 capturado"). */
  secondary?: string;
  /** Tooltip explicando o KPI quando o nome sozinho não basta. */
  hint?: string;
  delta: number | null;
  deltaSuffix: string;
  deltaInverse?: boolean;
  spark: number[] | null;
  sparkColor: string;
  Icon: React.ComponentType<{ size?: number }>;
  /** Cor da pill gradient gigante (Dokue-style). Rotaciona entre os 4 cards
   *  pra dar variedade visual no painel sem deixar o brand monocromático. */
  tone: MetricTone;
}

// Ícones de KPI — outline, dimensionáveis via prop. Herdam cor via currentColor.
// Todos têm conteúdo normalizado em x=3..21 (18 unidades de 24) pra que, quando
// usados como marca d'água em tamanho grande, fiquem visualmente do mesmo tamanho
// quando ancorados pela mesma posição. Renderizar SVGs com conteúdo de larguras
// diferentes (ex: cifrão de 11u vs ticket de 20u) cria a sensação de "alguns
// estão maiores que outros" mesmo com a mesma largura de SVG.
function IconReceitaTotal({ size = 20 }: { size?: number }) {
  // Cifrão clássico (linha vertical + S-curve). Mantemos esse desenho mesmo que
  // o conteúdo seja mais estreito (~11u vs 18u dos outros) — é o símbolo mais
  // legível como "$ = receita" sem precisar embalar em círculo.
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <line x1="12" y1="2" x2="12" y2="22" />
      <path d="M17 5H9.5a3.5 3.5 0 0 0 0 7h5a3.5 3.5 0 0 1 0 7H6" />
    </svg>
  );
}
function IconReceitaLiquida({ size = 20 }: { size?: number }) {
  // Carteira billfold com bolsinho de moeda na lateral direita. Caminhos
  // fechados pra que o contorno renderize completo (a versão anterior deixava
  // gaps no canto inferior porque os paths não se conectavam).
  // Largura x=3..21 (com bolsinho indo até x=20).
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M20 12V7H5a2 2 0 0 1 0-4h13v4" />
      <path d="M3 5v14a2 2 0 0 0 2 2h15v-5" />
      <path d="M16 12a2 2 0 0 0 0 4h4v-4Z" />
    </svg>
  );
}
function IconTransacoes({ size = 20 }: { size?: number }) {
  // Setas opostas — x=3..21 nativamente, já normalizado.
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="m17 3 4 4-4 4" />
      <path d="M21 7H3" />
      <path d="m7 21-4-4 4-4" />
      <path d="M3 17h18" />
    </svg>
  );
}
function IconTicket({ size = 20 }: { size?: number }) {
  // Ticket trazido pra x=3..21 (era 2..22) pra alinhar com os demais.
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M5 5h14a2 2 0 0 1 2 2v2a2 2 0 0 0 0 4v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-3a2 2 0 0 0 0-4V7a2 2 0 0 1 2-2Z" />
      <path d="M9 9v6" />
      <path d="M15 9v6" />
    </svg>
  );
}

function deltaPercent(current: number, previous: number): number | null {
  if (previous === 0) return current === 0 ? 0 : null;
  return ((current - previous) / Math.abs(previous)) * 100;
}

function buildMetrics(
  current: SellerDashboardSummary,
  previous: SellerDashboardSummary | null,
  points: DashboardTimeseriesPoint[] | null
): MetricDef[] {
  // KPIs financeiros só consideram capturadas — tentativas recusadas/pendentes
  // não são "receita" e contaminariam a leitura. O backend já entrega esses
  // agregados separados (CapturedVolume/CapturedNet/CapturedCount).
  const capturedNow = current.capturedVolume;
  const capturedNetNow = current.capturedNet;
  const capturedCountNow = current.capturedCount;

  const capturedPrev = previous?.capturedVolume ?? 0;
  const capturedNetPrev = previous?.capturedNet ?? 0;
  const capturedCountPrev = previous?.capturedCount ?? 0;

  const ticketCurrent = capturedCountNow > 0 ? capturedNow / capturedCountNow : 0;
  const ticketPrev = capturedCountPrev > 0 ? capturedPrev / capturedCountPrev : 0;

  const volumeSpark = points?.map((p) => p.volume) ?? null;
  const netSpark = points?.map((p) => p.net) ?? null;
  const countSpark = points?.map((p) => p.count) ?? null;
  const ticketSpark = points?.map((p) => (p.count > 0 ? p.volume / p.count : 0)) ?? null;

  const BRAND = "#7B61FF";

  return [
    {
      label: "Receita Total",
      value: formatCurrency(capturedNow),
      hint: "Soma bruta das transações capturadas — dinheiro que efetivamente entrou (antes das taxas).",
      delta: previous ? deltaPercent(capturedNow, capturedPrev) : null,
      deltaSuffix: "%",
      spark: volumeSpark,
      sparkColor: BRAND,
      Icon: IconReceitaTotal,
      tone: "brand" as const,
    },
    {
      label: "Receita Líquida",
      value: formatCurrency(capturedNetNow),
      hint: "Receita das capturadas já descontada das taxas — quanto você de fato fica.",
      delta: previous ? deltaPercent(capturedNetNow, capturedNetPrev) : null,
      deltaSuffix: "%",
      spark: netSpark,
      sparkColor: BRAND,
      Icon: IconReceitaLiquida,
      tone: "success" as const,
    },
    {
      label: "Transações",
      value: String(capturedCountNow),
      hint: "Apenas transações capturadas no período.",
      delta: previous ? deltaPercent(capturedCountNow, capturedCountPrev) : null,
      deltaSuffix: "%",
      spark: countSpark,
      sparkColor: BRAND,
      Icon: IconTransacoes,
      tone: "blue" as const,
    },
    {
      label: "Ticket Médio",
      value: formatCurrency(ticketCurrent),
      delta: previous ? deltaPercent(ticketCurrent, ticketPrev) : null,
      deltaSuffix: "%",
      spark: ticketSpark,
      sparkColor: BRAND,
      Icon: IconTicket,
      tone: "amber" as const,
    },
  ];
}

function DeltaBadge({ delta, inverse, suffix }: { delta: number; inverse?: boolean; suffix: string }) {
  const positive = delta >= 0;
  const isGood = inverse ? !positive : positive;
  const color = isGood
    ? "text-success-700 bg-success-50 dark:text-success-400 dark:bg-success-500/10"
    : "text-error-700 bg-error-50 dark:text-error-400 dark:bg-error-500/10";
  const sign = positive ? "+" : "";
  return (
    <span className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-semibold tabular-nums ${color}`}>
      <svg width="9" height="9" viewBox="0 0 8 8" fill="none" aria-hidden="true">
        {positive ? (
          <path d="M4 1L7 5H1L4 1Z" fill="currentColor" />
        ) : (
          <path d="M4 7L1 3H7L4 7Z" fill="currentColor" />
        )}
      </svg>
      {sign}
      {delta.toFixed(1)}
      {suffix}
    </span>
  );
}

function InfoIcon() {
  return (
    <svg
      width="13"
      height="13"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      className="cursor-help opacity-60 hover:opacity-100 transition-opacity"
    >
      <circle cx="12" cy="12" r="10" />
      <path d="M12 16v-4" />
      <path d="M12 8h.01" />
    </svg>
  );
}

export function DashboardMetrics() {
  const { current, previous, loading, error } = useDashboardSummary();
  const { data: timeseries } = useDashboardTimeseries();

  if (loading) {
    return (
      <div className="grid grid-cols-2 gap-3 xl:grid-cols-4">
        {[...Array(4)].map((_, i) => (
          <div
            key={i}
            className="rounded-3xl border border-gray-200/60 bg-white p-5 dark:border-gray-800 dark:bg-gray-900 animate-pulse"
          >
            <div className="h-3 w-20 bg-gray-200 dark:bg-gray-700 rounded mb-4" />
            <div className="h-8 w-32 bg-gray-200 dark:bg-gray-700 rounded mb-2" />
            <div className="h-3 w-16 bg-gray-100 dark:bg-gray-800 rounded mb-4" />
            <div className="h-10 bg-gray-100 dark:bg-gray-800 rounded" />
          </div>
        ))}
      </div>
    );
  }

  if (error || !current) {
    return (
      <div className="rounded-xl border border-error-200 bg-error-50 dark:border-error-800 dark:bg-error-900/20 p-4">
        <p className="text-sm text-error-700 dark:text-error-400">
          {error || "Não foi possível carregar métricas."}
        </p>
      </div>
    );
  }

  const metrics = buildMetrics(current, previous, timeseries?.points ?? null);

  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
      {metrics.map((m) => {
        const Icon = m.Icon;
        // Cada tone tem sua classe pill-gradient-{tone} (definida em globals.css).
        // Aplica no <div> da pill grande que carrega o número, com shadow tinted.
        const pillCls = {
          brand: "pill-gradient-brand",
          success: "pill-gradient-success",
          blue: "pill-gradient-blue",
          amber: "pill-gradient-amber",
        }[m.tone];
        return (
          <div
            key={m.label}
            className="group relative rounded-3xl border border-gray-200/60 bg-white p-4 dark:border-gray-800 dark:bg-gray-900 flex flex-col transition-all hover:shadow-[0_8px_28px_-12px_rgba(0,0,0,0.12)] dark:hover:shadow-[0_8px_28px_-12px_rgba(0,0,0,0.4)]"
          >
            {/* Header Dokue: label em sentence-case com peso normal (não bold,
                não uppercase). Cor preta/escura — não cinza apagado. Ícone
                discreto top-right num quadrado off-white. */}
            <div className="flex items-start justify-between mb-3 gap-2">
              <div className="min-w-0 flex-1 flex items-start gap-1.5">
                <p
                  className="text-[15px] font-medium text-gray-900 dark:text-white leading-tight"
                  title={m.hint}
                >
                  {m.label}
                </p>
                {m.hint && <span className="mt-0.5"><InfoIcon /></span>}
              </div>
              <div className="shrink-0 inline-flex items-center justify-center w-8 h-8 rounded-xl bg-gray-50 dark:bg-gray-800 text-gray-400 dark:text-gray-500">
                <Icon size={16} />
              </div>
            </div>

            {/* Layout Dokue: pill decorativa pequena + número em escala
                modesta (text-lg ~ semibold). Não bold gigante. tabular-nums
                pra alinhamento. */}
            <div className="flex items-center gap-3">
              <div
                aria-hidden="true"
                className={`${pillCls} h-8 w-14 rounded-full shrink-0`}
              />
              <span className="text-lg font-semibold text-gray-900 dark:text-white tabular-nums tracking-tight leading-none truncate">
                {m.value}
              </span>
            </div>

            {/* Secondary à esquerda + Delta badge à direita, embaixo do
                número. justify-between empurra as duas pontas (delta no
                right side do card, secondary na esquerda se houver). */}
            <div className="mt-2.5 flex items-center justify-between gap-2 min-h-[18px]">
              {m.secondary ? (
                <span className="text-[11px] text-gray-500 dark:text-gray-400 tabular-nums truncate">
                  {m.secondary}
                </span>
              ) : (
                <span />
              )}
              {m.delta !== null && Number.isFinite(m.delta) && (
                <DeltaBadge delta={m.delta} inverse={m.deltaInverse} suffix={m.deltaSuffix} />
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
}
