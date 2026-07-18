"use client";
import React, { useEffect, useMemo, useState } from "react";
import { splitRulesService } from "@/services/split-rules.service";
import { Select } from "@/components/ui/Select";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { getCurrentSellerId } from "@/context/AuthContext";
import { PageHeader } from "@/components/ui/PageHeader";
import type { SimulateSplitResponse, SplitRule, SimulateSplitRecipient, PaymentType, FeeAllocationPolicy } from "@/types";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

type DivisionMode = "none" | "rule" | "manual";

interface ManualSplit {
  sellerId: string;
  /** "PERCENTAGE" | "FIXED" — controla qual campo é editável. */
  kind: "PERCENTAGE" | "FIXED";
  value: string;
}

const PAYMENT_TYPES: { value: PaymentType; label: string }[] = [
  { value: "PIX", label: "Pix" },
  { value: "CREDIT_CARD", label: "Cartão de Crédito" },
  { value: "DEBIT_CARD", label: "Cartão de Débito" },
  { value: "BOLETO", label: "Boleto" },
];

const POLICIES: { value: FeeAllocationPolicy; label: string; hint: string }[] = [
  { value: "PRIMARY_SELLER_PAYS_FEES", label: "Seller principal paga taxas", hint: "Recipients recebem o valor cheio; a taxa sai do residual." },
  { value: "PROPORTIONAL_TO_RECIPIENTS", label: "Proporcional aos participantes", hint: "Cada recipient absorve a parte proporcional da taxa." },
  { value: "PLATFORM_ABSORBS", label: "Plataforma absorve", hint: "Nenhum participante paga taxa (uso interno/promocional)." },
];

export default function SplitSimulatorPage() {
  const mySellerId = getCurrentSellerId();

  const [amount, setAmount] = useState("");
  const [paymentType, setPaymentType] = useState<PaymentType>("PIX");
  const [installments, setInstallments] = useState("1");
  const [feePolicy, setFeePolicy] = useState<FeeAllocationPolicy>("PRIMARY_SELLER_PAYS_FEES");

  const [mode, setMode] = useState<DivisionMode>("none");
  const [selectedRuleId, setSelectedRuleId] = useState("");
  const [manualSplits, setManualSplits] = useState<ManualSplit[]>([]);

  const [rules, setRules] = useState<SplitRule[]>([]);
  const [rulesLoading, setRulesLoading] = useState(true);

  const [isLoading, setIsLoading] = useState(false);
  const [result, setResult] = useState<SimulateSplitResponse | null>(null);
  const [error, setError] = useState("");

  // Parcelas só fazem sentido em crédito — força 1× no resto, igual /payment-links.
  const installmentsDisabled = paymentType !== "CREDIT_CARD";
  useEffect(() => {
    if (installmentsDisabled && installments !== "1") setInstallments("1");
  }, [installmentsDisabled, installments]);

  useEffect(() => {
    let cancelled = false;
    splitRulesService
      .list()
      .then((all) => {
        if (cancelled) return;
        // Só regras ativas e onde o user logado é o owner. Se logar como admin
        // sem seller, fallback é mostrar todas as ativas (caso raro).
        const filtered = all.filter((r) => r.isActive && (mySellerId ? r.ownerSellerId === mySellerId : true));
        setRules(filtered);
      })
      .catch(() => setRules([]))
      .finally(() => !cancelled && setRulesLoading(false));
    return () => {
      cancelled = true;
    };
  }, [mySellerId]);

  const ruleOptions = useMemo(
    () => [
      { value: "", label: rulesLoading ? "Carregando regras..." : (rules.length > 0 ? "Selecione uma regra…" : "Nenhuma regra ativa") },
      ...rules.map((r) => ({ value: r.id, label: r.name })),
    ],
    [rules, rulesLoading]
  );

  const addManualSplit = () => {
    setManualSplits((prev) => [...prev, { sellerId: "", kind: "PERCENTAGE", value: "" }]);
  };
  const updateManualSplit = (idx: number, patch: Partial<ManualSplit>) => {
    setManualSplits((prev) => prev.map((s, i) => (i === idx ? { ...s, ...patch } : s)));
  };
  const removeManualSplit = (idx: number) => {
    setManualSplits((prev) => prev.filter((_, i) => i !== idx));
  };

  const validateBeforeSubmit = (): string | null => {
    if (!mySellerId) return "Esta sessão não está vinculada a um seller. Faça login com uma conta de seller para simular splits.";
    if (mode === "rule" && !selectedRuleId) return "Selecione uma regra cadastrada para simular.";
    if (mode === "manual") {
      if (manualSplits.length === 0) return "Adicione pelo menos um recipient para o modo manual.";
      for (const [i, s] of manualSplits.entries()) {
        if (!s.sellerId.trim()) return `Recipient ${i + 1}: informe o Seller ID.`;
        const v = parseFloat(s.value);
        if (!v || v <= 0) return `Recipient ${i + 1}: informe um valor maior que zero.`;
        if (s.kind === "PERCENTAGE" && v > 100) return `Recipient ${i + 1}: porcentagem não pode exceder 100.`;
      }
    }
    return null;
  };

  const handleSimulate = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");
    setResult(null);

    const validationError = validateBeforeSubmit();
    if (validationError) {
      setError(validationError);
      return;
    }

    setIsLoading(true);
    try {
      const splits: SimulateSplitRecipient[] | undefined =
        mode === "manual"
          ? manualSplits.map((s) => ({
              sellerId: s.sellerId.trim(),
              ...(s.kind === "PERCENTAGE"
                ? { percentage: parseFloat(s.value) }
                : { amount: parseFloat(s.value) }),
            }))
          : undefined;

      const res = await splitRulesService.simulate({
        sellerId: mySellerId!,
        amount: parseFloat(amount),
        paymentType,
        installments: parseInt(installments) || 1,
        // Política só vai pro backend quando há divisão.
        feeAllocationPolicy: mode === "none" ? undefined : feePolicy,
        splitRuleId: mode === "rule" ? selectedRuleId : undefined,
        splits,
      });
      setResult(res);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Erro ao simular split.");
    }
    setIsLoading(false);
  };

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Simulador de Split"
        subtitle="Simule como um pagamento seria dividido — sem cobrar nada de verdade. Útil para validar regras antes de aplicar a um link de pagamento."
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <rect x="4" y="2" width="16" height="20" rx="2" />
            <line x1="8" y1="6" x2="16" y2="6" />
            <line x1="8" y1="10" x2="9" y2="10" />
            <line x1="12" y1="10" x2="13" y2="10" />
            <line x1="16" y1="10" x2="16" y2="10" />
            <line x1="8" y1="14" x2="9" y2="14" />
            <line x1="12" y1="14" x2="13" y2="14" />
            <line x1="16" y1="14" x2="16" y2="18" />
            <line x1="8" y1="18" x2="13" y2="18" />
          </svg>
        }
      />

      <div className="grid grid-cols-1 xl:grid-cols-12 gap-6">
        {/* Inputs */}
        <div className="xl:col-span-5 rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
          <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-4">Parâmetros</h2>
          <form onSubmit={handleSimulate} className="space-y-4">
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Valor (R$)</label>
                <input
                  type="number"
                  step="0.01"
                  min="0.01"
                  value={amount}
                  onChange={(e) => setAmount(e.target.value)}
                  className="w-full rounded-lg border border-gray-200 px-4 py-2.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-white focus:border-brand-500 focus:outline-none"
                  placeholder="100,00"
                  required
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Parcelas</label>
                <input
                  type="number"
                  min="1"
                  max="12"
                  value={installments}
                  onChange={(e) => setInstallments(e.target.value)}
                  disabled={installmentsDisabled}
                  className="w-full rounded-lg border border-gray-200 px-4 py-2.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-white focus:border-brand-500 focus:outline-none disabled:opacity-50 disabled:cursor-not-allowed"
                />
                {installmentsDisabled && (
                  <p className="mt-1 text-[11px] text-gray-500 dark:text-gray-400">Apenas para crédito.</p>
                )}
              </div>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Método de Pagamento</label>
              <Select
                value={paymentType}
                onChange={(v) => setPaymentType(v as PaymentType)}
                options={PAYMENT_TYPES}
              />
            </div>

            {/* Modo de divisão */}
            <div>
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-2">Divisão</label>
              <div className="grid grid-cols-3 gap-2">
                {([
                  { value: "none", label: "Sem divisão" },
                  { value: "rule", label: "Regra cadastrada" },
                  { value: "manual", label: "Manual" },
                ] as { value: DivisionMode; label: string }[]).map((opt) => (
                  <button
                    key={opt.value}
                    type="button"
                    onClick={() => setMode(opt.value)}
                    className={`rounded-lg border px-3 py-2 text-xs font-medium transition-colors ${
                      mode === opt.value
                        ? "border-brand-500 bg-brand-50 text-brand-700 dark:bg-brand-500/10 dark:text-brand-300"
                        : "border-gray-200 text-gray-600 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
                    }`}
                  >
                    {opt.label}
                  </button>
                ))}
              </div>
            </div>

            {mode === "rule" && (
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Regra ativa</label>
                <Select
                  value={selectedRuleId}
                  onChange={(v) => setSelectedRuleId(v)}
                  options={ruleOptions}
                />
                {!rulesLoading && rules.length === 0 && (
                  <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">
                    Você ainda não tem regras ativas. Crie em <a href="/split-rules" className="text-brand-600 hover:underline">Split Rules</a>.
                  </p>
                )}
              </div>
            )}

            {mode === "manual" && (
              <div className="space-y-3">
                <div className="flex items-center justify-between">
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300">Recipients</label>
                  <button type="button" onClick={addManualSplit} className="text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400">
                    + Adicionar
                  </button>
                </div>
                {manualSplits.length === 0 && (
                  <p className="text-xs text-gray-500 dark:text-gray-400">Adicione recipients para simular uma divisão sem precisar cadastrar regra.</p>
                )}
                {manualSplits.map((s, i) => (
                  <div key={i} className="rounded-lg border border-gray-100 dark:border-gray-800 p-3 space-y-2">
                    <div className="flex items-center justify-between">
                      <span className="text-xs font-medium text-gray-500 dark:text-gray-400">#{i + 1}</span>
                      <button type="button" onClick={() => removeManualSplit(i)} className="text-xs text-error-600 hover:text-error-700 dark:text-error-400">
                        Remover
                      </button>
                    </div>
                    <input
                      type="text"
                      value={s.sellerId}
                      onChange={(e) => updateManualSplit(i, { sellerId: e.target.value })}
                      placeholder="Seller ID (UUID)"
                      className="w-full rounded-lg border border-gray-200 px-3 py-2 text-xs font-mono dark:border-gray-700 dark:bg-gray-800 dark:text-white focus:border-brand-500 focus:outline-none"
                    />
                    <div className="grid grid-cols-3 gap-2">
                      <Select
                        value={s.kind}
                        onChange={(v) => updateManualSplit(i, { kind: v as "PERCENTAGE" | "FIXED", value: "" })}
                        options={[
                          { value: "PERCENTAGE", label: "%" },
                          { value: "FIXED", label: "R$" },
                        ]}
                      />
                      <input
                        type="number"
                        step="0.01"
                        min="0"
                        max={s.kind === "PERCENTAGE" ? 100 : undefined}
                        value={s.value}
                        onChange={(e) => updateManualSplit(i, { value: e.target.value })}
                        placeholder={s.kind === "PERCENTAGE" ? "0-100" : "0,00"}
                        className="col-span-2 rounded-lg border border-gray-200 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-white focus:border-brand-500 focus:outline-none"
                      />
                    </div>
                  </div>
                ))}
              </div>
            )}

            {mode !== "none" && (
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Quem paga as taxas</label>
                <Select
                  value={feePolicy}
                  onChange={(v) => setFeePolicy(v as FeeAllocationPolicy)}
                  options={POLICIES.map(({ value, label }) => ({ value, label }))}
                />
                <p className="mt-1 text-[11px] text-gray-500 dark:text-gray-400">
                  {POLICIES.find((p) => p.value === feePolicy)?.hint}
                </p>
              </div>
            )}

            <button
              type="submit"
              disabled={isLoading}
              className="w-full rounded-lg bg-brand-500 px-4 py-2.5 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50 transition-colors"
            >
              {isLoading ? "Simulando..." : "Simular"}
            </button>
          </form>
        </div>

        {/* Resultado */}
        <div className="xl:col-span-7 space-y-4">
          {error && (
            <div className="rounded-xl border border-error-200 bg-error-50 dark:border-error-500/30 dark:bg-error-500/10 p-4 text-sm text-error-700 dark:text-error-400">
              {error}
            </div>
          )}

          {!result && !error && (
            <div className="rounded-xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900 p-8 text-center">
              <p className="text-sm text-gray-500 dark:text-gray-400">
                Preencha os parâmetros e clique em <strong>Simular</strong> para ver a divisão.
              </p>
            </div>
          )}

          {result && (
            <>
              {/* Sumário do pagamento — só os 3 números relevantes pro seller.
                  Custo do gateway e margem da plataforma são informação interna e
                  não aparecem aqui. */}
              <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
                <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-4">Cálculo do pagamento</h2>
                <dl className="space-y-3 text-sm">
                  <div className="flex justify-between border-b border-gray-100 dark:border-gray-800 pb-2">
                    <dt className="text-gray-500 dark:text-gray-400">Valor bruto</dt>
                    <dd className="text-gray-900 dark:text-white font-medium">{formatCurrency(result.grossAmount)}</dd>
                  </div>
                  <div className="flex justify-between border-b border-gray-100 dark:border-gray-800 pb-2">
                    <dt className="text-gray-500 dark:text-gray-400">Taxa cobrada</dt>
                    <dd className="text-gray-900 dark:text-white">{formatCurrency(result.platformFee)}</dd>
                  </div>
                  <div className="flex justify-between pt-1">
                    <dt className="text-gray-700 dark:text-gray-200 font-semibold">Líquido total</dt>
                    <dd className="text-gray-900 dark:text-white font-bold text-base">{formatCurrency(result.netAmount)}</dd>
                  </div>
                </dl>
              </div>

              {/* Recipients (split) */}
              {result.recipients.length > 0 && (
                <div className="rounded-xl border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
                  <h2 className="text-sm font-medium text-gray-900 dark:text-white mb-3">Divisão entre participantes</h2>
                  <div className="space-y-2">
                    {result.recipients.map((r, i) => (
                      <div key={i} className="flex items-center justify-between rounded-lg bg-gray-50 dark:bg-gray-800/50 p-3">
                        <div className="min-w-0">
                          <IdDisplay id={r.sellerId} mineId={mySellerId} copyable />
                        </div>
                        <div className="text-right whitespace-nowrap">
                          <p className="text-sm font-semibold text-gray-900 dark:text-white">{formatCurrency(r.netShare)}</p>
                          <p className="text-[11px] text-gray-500 dark:text-gray-400">
                            bruto {formatCurrency(r.grossShare)} · taxa {formatCurrency(r.feeShare)}
                          </p>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {/* Você (residual) */}
              <div className="rounded-xl border border-brand-200 bg-brand-50 dark:border-brand-500/30 dark:bg-brand-500/10 p-5">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-xs uppercase tracking-wider text-brand-700 dark:text-brand-300">Você recebe (residual)</p>
                    <div className="mt-0.5">
                      <IdDisplay id={result.primaryResidual.sellerId} mineId={mySellerId} mineLabel="Você" />
                    </div>
                  </div>
                  <p className="text-2xl font-bold text-brand-700 dark:text-brand-300">
                    {formatCurrency(result.primaryResidual.amount)}
                  </p>
                </div>
              </div>

              {/* Avisos / arredondamento */}
              {result.warnings.length > 0 && (
                <div className="rounded-xl border border-warning-200 bg-warning-50 dark:border-warning-500/30 dark:bg-warning-500/10 p-4">
                  <p className="text-xs font-semibold text-warning-700 dark:text-warning-300 uppercase tracking-wider mb-2">Avisos</p>
                  <ul className="space-y-1">
                    {result.warnings.map((w, i) => (
                      <li key={i} className="text-sm text-warning-700 dark:text-warning-300">{w}</li>
                    ))}
                  </ul>
                </div>
              )}
              {result.roundingAdjustment !== 0 && (
                <p className="text-xs text-gray-500 dark:text-gray-400">
                  Ajuste de arredondamento aplicado ao residual: {formatCurrency(result.roundingAdjustment)}.
                </p>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
}
