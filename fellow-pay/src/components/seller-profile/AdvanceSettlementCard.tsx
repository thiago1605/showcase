"use client";

import React, { useState } from "react";
import type { SellerProfile, UpdateSellerProfileRequest } from "@/types";

interface Props {
  profile: SellerProfile;
  onSave: (patch: UpdateSellerProfileRequest) => Promise<SellerProfile>;
}

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

function ShieldIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" />
    </svg>
  );
}

function ClockIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <circle cx="12" cy="12" r="10" />
      <polyline points="12 6 12 12 16 14" />
    </svg>
  );
}

/**
 * Toggle de antecipação automática (Modelo Híbrido). Quando ligado, TODAS as TXs
 * de crédito do seller geram 1 parcela única em D+30 (cobra advance fee do plano).
 * Quando desligado, segue o fluxo padrão de parcelas mensais.
 *
 * UX: optimistic update com rollback em erro de rede / 4xx do backend.
 * Visibilidade do limite de antecipação aprovado para que o seller entenda quando
 * o sistema vai cair em INSTALLMENT (fallback silencioso por limit_reached).
 */
export default function AdvanceSettlementCard({ profile, onSave }: Props) {
  const initialEnabled = profile.autoAdvanceSettlement ?? false;
  const limit = profile.advanceCreditLimit ?? 0;
  const exposure = profile.advanceExposureCurrent ?? 0;
  const headroom = Math.max(0, limit - exposure);
  const hasApprovedLimit = limit > 0;

  const [enabled, setEnabled] = useState(initialEnabled);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleToggle(next: boolean) {
    if (saving) return;
    const previous = enabled;
    setEnabled(next); // optimistic
    setSaving(true);
    setError(null);
    try {
      await onSave({ autoAdvanceSettlement: next });
    } catch (e) {
      setEnabled(previous); // rollback
      const msg = e instanceof Error ? e.message : "Erro ao salvar.";
      setError(msg);
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900">
      <div className="px-5 lg:px-6 py-4 border-b border-gray-200/80 dark:border-gray-800">
        <div className="flex items-center gap-2">
          <span className="text-brand-500 dark:text-brand-400"><ShieldIcon /></span>
          <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Antecipação automática</h3>
        </div>
        <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 leading-relaxed">
          Quando ativa, vendas no crédito são liberadas em D+30 em uma única
          parcela (em vez de mensalmente). Aplica taxa de antecipação do plano.
        </p>
      </div>

      <div className="p-5 lg:p-6 space-y-4">
        {/* Toggle */}
        <div className="flex items-start justify-between gap-4">
          <div className="flex-1 min-w-0">
            <p className="text-sm font-medium text-gray-900 dark:text-white">
              Ativar antecipação automática
            </p>
            <p className="text-[11px] text-gray-500 dark:text-gray-400 mt-0.5">
              Mudanças não afetam transações já capturadas.
            </p>
          </div>
          <label className="relative inline-flex cursor-pointer items-center">
            <input
              type="checkbox"
              className="peer sr-only"
              checked={enabled}
              onChange={(e) => handleToggle(e.target.checked)}
              disabled={saving || !hasApprovedLimit}
              aria-label="Ativar antecipação automática"
            />
            <div
              className={`h-6 w-11 rounded-full transition-colors ${
                enabled
                  ? "bg-brand-500"
                  : "bg-gray-300 dark:bg-gray-700"
              } ${(!hasApprovedLimit || saving) ? "opacity-50 cursor-not-allowed" : ""}`}
            >
              <div
                className={`absolute top-0.5 left-0.5 h-5 w-5 rounded-full bg-white shadow-sm transition-transform ${
                  enabled ? "translate-x-5" : "translate-x-0"
                }`}
              />
            </div>
          </label>
        </div>

        {error && (
          <p className="text-xs text-error-600 dark:text-error-400 bg-error-50 dark:bg-error-900/20 border border-error-200 dark:border-error-800/40 rounded-lg px-3 py-2">
            {error}
          </p>
        )}

        {/* Status do limite */}
        <div className="pt-4 border-t border-gray-100 dark:border-gray-800 space-y-3">
          <div className="flex items-center gap-2">
            <span className="text-gray-400"><ClockIcon /></span>
            <p className="text-[11px] uppercase tracking-wide font-medium text-gray-500 dark:text-gray-400">
              Limite aprovado
            </p>
          </div>

          {!hasApprovedLimit ? (
            <div className="rounded-lg bg-amber-50 dark:bg-amber-900/20 border border-amber-200 dark:border-amber-800/40 px-3 py-2.5">
              <p className="text-xs font-medium text-amber-700 dark:text-amber-300">
                Antecipação ainda não disponível para esta conta.
              </p>
              <p className="text-[11px] text-amber-600 dark:text-amber-400 mt-1 leading-snug">
                Após um período de uso e histórico saudável, vamos liberar antecipação automaticamente.
              </p>
            </div>
          ) : (
            <div className="grid grid-cols-2 gap-3">
              <div>
                <p className="text-[10px] uppercase font-medium text-gray-400">Disponível para antecipar</p>
                <p className="text-base font-semibold text-gray-900 dark:text-white tabular-nums mt-0.5">
                  {formatCurrency(headroom)}
                </p>
              </div>
              <div>
                <p className="text-[10px] uppercase font-medium text-gray-400">Em uso (não recuperado)</p>
                <p className="text-base font-medium text-gray-600 dark:text-gray-400 tabular-nums mt-0.5">
                  {formatCurrency(exposure)}
                </p>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
