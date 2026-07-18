"use client";
import React, { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { dashboardService } from "@/services/dashboard.service";
import type { SellerBalance, SellerReleaseBuckets } from "@/types";
import { Sparkline } from "./Sparkline";

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

/**
 * Converte buckets cumulativos do backend em janelas mutuamente exclusivas.
 * Cada bucket aqui = quanto libera DENTRO daquela janela específica, sem somar
 * com as anteriores. Resultado: as linhas que vão aparecer no UI somam exatamente
 * o `next365Days` (que é o total previsto pelo backend).
 *
 * Backend manda: next7 ⊇ next2 (cumulativo). Pra UI, queremos:
 *   [0–7d] = next7
 *   [8–30d] = next30 − next7
 *   [31–90d] = next90 − next30
 *   ...etc
 */
interface DeltaBucket {
  label: string;
  amount: number;
}

function computeDeltaBuckets(b: SellerReleaseBuckets): DeltaBucket[] {
  // Note: next2 absorvido em next7 porque pra UX as duas janelas separadas
  // (2d e 7d) confundem mais do que ajudam — débito Stripe BR é D+2 mas
  // PIX/boleto via Stripe é D+0/D+2, todos caem no mesmo "esta semana".
  return [
    { label: "Em até 7 dias",        amount: b.next7Days },
    { label: "De 8 a 30 dias",       amount: b.next30Days  - b.next7Days  },
    { label: "De 1 a 3 meses",       amount: b.next90Days  - b.next30Days },
    { label: "De 3 a 6 meses",       amount: b.next180Days - b.next90Days },
    { label: "De 6 a 12 meses",      amount: b.next365Days - b.next180Days },
  ];
}

/**
 * Sparkline mostra liberação DELTA por janela (não cumulativa). Cada barra =
 * quanto entra naquele período. Não plota janelas com R$ 0 pra evitar linhas planas.
 */
function buildDeltaSeries(deltas: DeltaBucket[]): number[] {
  // Apenas valores > 0 (ou pelo menos > 0.01 — float dance)
  return deltas.filter((d) => d.amount > 0.01).map((d) => Number(d.amount.toFixed(2)));
}

interface BucketRowProps {
  label: string;
  amount: number;
  variant?: "default" | "drift";
}

function BucketRow({ label, amount, variant = "default" }: BucketRowProps) {
  const isDrift = variant === "drift";
  return (
    <div className="flex items-center justify-between gap-3 py-1.5">
      <span
        className={
          isDrift
            ? "text-[11px] font-medium text-amber-600 dark:text-amber-400"
            : "text-[11px] text-gray-500 dark:text-gray-400"
        }
      >
        {label}
      </span>
      <span
        className={
          isDrift
            ? "text-sm font-medium text-amber-700 dark:text-amber-300 tabular-nums"
            : "text-sm font-medium text-gray-800 dark:text-gray-200 tabular-nums"
        }
      >
        {formatCurrency(amount)}
      </span>
    </div>
  );
}

export function SellerBalanceCard() {
  const [balance, setBalance] = useState<SellerBalance | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showDetails, setShowDetails] = useState(false);

  useEffect(() => {
    dashboardService
      .getBalance()
      .then(setBalance)
      .catch((err) => setError(err.message || "Erro ao carregar saldo"))
      .finally(() => setLoading(false));
  }, []);

  const deltaBuckets = useMemo(
    () => (balance?.blockedBuckets ? computeDeltaBuckets(balance.blockedBuckets) : []),
    [balance]
  );

  // Filtra zeros pra UI compacta — só mostra janelas que de fato têm dinheiro entrando.
  const nonZeroDeltas = useMemo(
    () => deltaBuckets.filter((d) => d.amount > 0.01),
    [deltaBuckets]
  );

  // Drift entre o que o Stripe diz (blocked) e o que somamos dos buckets.
  // Positivo: temos previsão mas Stripe diz menos bloqueado (reversal/payout que não conciliamos).
  // Negativo: Stripe diz mais bloqueado do que temos TXs explicando.
  const scheduleSum = useMemo(
    () => deltaBuckets.reduce((acc, d) => acc + d.amount, 0),
    [deltaBuckets]
  );
  const drift = (balance?.blocked ?? 0) - scheduleSum;

  const sparklineData = useMemo(() => buildDeltaSeries(deltaBuckets), [deltaBuckets]);

  if (loading) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full animate-pulse">
        <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
          <div className="h-4 w-16 bg-gray-200 dark:bg-gray-700 rounded" />
        </div>
        <div className="p-5 space-y-4">
          <div className="h-8 w-32 bg-gray-200 dark:bg-gray-700 rounded" />
          <div className="h-6 w-28 bg-gray-200 dark:bg-gray-700 rounded" />
          <div className="h-5 w-24 bg-gray-200 dark:bg-gray-700 rounded" />
        </div>
      </div>
    );
  }

  if (error || !balance) {
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full">
        <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Saldo</h3>
        </div>
        <div className="p-5">
          <p className="text-sm text-error-600 dark:text-error-400">
            {error || "Não foi possível carregar saldo."}
          </p>
        </div>
      </div>
    );
  }

  const hasBlocked = balance.blocked > 0;
  const hasSchedule = nonZeroDeltas.length > 0;
  // Drift é "interessante" só quando excede 1 centavo (acima do ruído de rounding).
  const hasDrift = Math.abs(drift) > 0.01;
  // Próxima janela com movimento — pra dar dica curta acima do detalhe.
  const nextRelease = nonZeroDeltas[0];

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full flex flex-col">
      <div className="px-5 py-4 border-b border-gray-200/80 dark:border-gray-800">
        <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Saldo</h3>
        <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
          Saldo atual (não filtrado pelo período)
        </p>
      </div>
      <div className="p-5 flex flex-col gap-5 flex-1">
        <div>
          <p className="text-[11px] uppercase tracking-wide font-medium text-gray-500 dark:text-gray-400 mb-1.5">
            Disponível para saque
          </p>
          <p className="text-[28px] leading-none font-semibold text-gray-900 dark:text-white tabular-nums tracking-tight">
            {formatCurrency(balance.available)}
          </p>
        </div>

        <div className="border-t border-gray-100 dark:border-gray-800 pt-4">
          <div className="flex items-center justify-between">
            <p className="text-[11px] uppercase tracking-wide font-medium text-gray-500 dark:text-gray-400">
              Bloqueado
            </p>
            {hasBlocked && hasSchedule && (
              <button
                type="button"
                onClick={() => setShowDetails((v) => !v)}
                className="text-[11px] font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400 dark:hover:text-brand-300"
              >
                {showDetails ? "Ocultar" : "Quando libera?"}
              </button>
            )}
          </div>
          <p className="text-base font-medium text-gray-700 dark:text-gray-300 tabular-nums mt-1.5">
            {formatCurrency(balance.blocked)}
          </p>
          {/* Dica curta — só a primeira janela ativa. Texto em um único nó
              pra facilitar test-by-text-match; label em lowercase pra não
              colidir com a versão capitalizada usada nas linhas detalhadas. */}
          {hasBlocked && nextRelease && (
            <p className="text-[11px] text-gray-500 dark:text-gray-400 mt-1">
              {`Próxima liberação: ${formatCurrency(nextRelease.amount)} — ${nextRelease.label.toLowerCase()}`}
            </p>
          )}

          {hasBlocked && sparklineData.length >= 2 && (
            <div className="mt-3 -mx-1">
              <Sparkline
                data={sparklineData}
                color="#b026ff"
                height={32}
                ariaLabel="Volume por janela de liberação"
              />
            </div>
          )}

          {hasBlocked && showDetails && (
            <div className="mt-3 pt-3 border-t border-gray-100 dark:border-gray-800">
              {nonZeroDeltas.length > 0 && (
                <div className="space-y-0.5">
                  {nonZeroDeltas.map((d) => (
                    <BucketRow key={d.label} label={d.label} amount={d.amount} />
                  ))}
                </div>
              )}

              {/* Drift — mostra com explicação curta pra não parecer bug.
                  Positivo = Stripe diz menos do que prevemos (reversal/payout não conciliado).
                  Negativo = Stripe diz mais bloqueado do que temos TX. */}
              {hasDrift && (
                <div
                  className={`${nonZeroDeltas.length > 0 ? "mt-2 pt-2 border-t border-dashed border-gray-200 dark:border-gray-700" : ""}`}
                >
                  <BucketRow
                    label={drift > 0 ? "Sem data prevista" : "Em conciliação"}
                    amount={drift}
                    variant="drift"
                  />
                  <p className="text-[10px] text-gray-400 dark:text-gray-500 leading-snug mt-1">
                    {drift > 0
                      ? "Saldo na Stripe sem TX correspondente no sistema (provisionado ou ajuste manual)."
                      : "Stripe estornou ou liberou parte do valor; sistema está reconciliando."}
                  </p>
                </div>
              )}

              {/* Confere — total das linhas = saldo bloqueado (depois do drift). */}
              <div className="mt-3 pt-2 border-t border-gray-100 dark:border-gray-800 flex items-center justify-between">
                <span className="text-[11px] font-medium text-gray-500 dark:text-gray-400">
                  Total bloqueado
                </span>
                <span className="text-sm font-semibold text-gray-900 dark:text-white tabular-nums">
                  {formatCurrency(balance.blocked)}
                </span>
              </div>
            </div>
          )}
        </div>

        <Link
          href="/payouts"
          className="block w-full mt-auto rounded-lg bg-brand-500 hover:bg-brand-600 active:scale-[0.998] px-4 py-3 text-sm font-medium text-white text-center transition-colors"
        >
          Solicitar Saque
        </Link>
      </div>
    </div>
  );
}
