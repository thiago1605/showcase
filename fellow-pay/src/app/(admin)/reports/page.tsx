"use client";
import React, { useState } from "react";
import { useScrollLock } from "@/hooks/useScrollLock";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { reportsService, ScheduledReport } from "@/services/reports.service";
import { Select } from "@/components/ui/Select";
import { CardListSkeleton } from "@/components/ui/Skeleton";
import { DeleteButton } from "@/components/ui/DeleteButton";
import { ConfirmModal } from "@/components/ui/ConfirmModal";
import Input from "@/components/form/input/InputField";

const typeOptions = [
  { value: "TRANSACTIONS", label: "Transações" },
  { value: "PAYOUTS", label: "Saques" },
  { value: "SUMMARY", label: "Resumo Geral" },
];

const formatOptions = [
  { value: "CSV", label: "CSV" },
  { value: "PDF", label: "PDF" },
];

const frequencyOptions = [
  { value: "DAILY", label: "Diário" },
  { value: "WEEKLY", label: "Semanal" },
  { value: "MONTHLY", label: "Mensal" },
];

export default function ReportsPage() {
  const queryClient = useQueryClient();
  const [showForm, setShowForm] = useState(false);
  const [formData, setFormData] = useState({ name: "", type: "TRANSACTIONS", format: "CSV", frequency: "WEEKLY" });
  const [formLoading, setFormLoading] = useState(false);
  const [formError, setFormError] = useState("");
  const [pendingAction, setPendingAction] = useState<{
    title: string;
    message: string;
    confirmLabel: string;
    run: () => Promise<void>;
  } | null>(null);
  const [pendingRunning, setPendingRunning] = useState(false);

  // Trava scroll enquanto o modal de criação de relatório estiver aberto.
  useScrollLock(showForm);

  const { data: reports = [], isLoading } = useQuery<ScheduledReport[]>({
    queryKey: ["reports", "list"],
    queryFn: () => reportsService.list(),
  });
  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["reports"] });

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    setFormError("");
    setFormLoading(true);
    try {
      await reportsService.create(formData);
      setShowForm(false);
      setFormData({ name: "", type: "TRANSACTIONS", format: "CSV", frequency: "WEEKLY" });
      invalidate();
    } catch {
      setFormError("Erro ao criar relatório.");
    }
    setFormLoading(false);
  };

  const handleDelete = (id: string, name: string) => {
    setPendingAction({
      title: "Remover relatório",
      message: `Remover permanentemente o relatório "${name}"? Esta ação não pode ser desfeita.`,
      confirmLabel: "Remover",
      run: async () => {
        await reportsService.delete(id);
        invalidate();
      },
    });
  };

  const handleToggle = (id: string, enabled: boolean, name: string) => {
    if (!enabled) {
      // Reativar é construtivo, sem confirmação.
      void reportsService.toggle(id, true).then(invalidate).catch(() => {});
      return;
    }
    setPendingAction({
      title: "Desativar relatório",
      message: `Desativar "${name}"? Os envios automáticos pararão até reativar.`,
      confirmLabel: "Desativar",
      run: async () => {
        await reportsService.toggle(id, false);
        invalidate();
      },
    });
  };

  const runPending = async () => {
    if (!pendingAction) return;
    setPendingRunning(true);
    try {
      await pendingAction.run();
      setPendingAction(null);
    } catch { /* silently fail */ }
    setPendingRunning(false);
  };

  if (isLoading) {
    return (
      <CardListSkeleton count={3} ariaLabel="Carregando relatórios" />
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold text-gray-900 dark:text-white">Relatórios</h1>
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">Configure relatórios automáticos das suas vendas.</p>
        </div>
        <button onClick={() => setShowForm(true)} className="inline-flex items-center gap-2 rounded-lg bg-brand-500 px-4 py-2.5 text-sm font-medium text-white hover:bg-brand-600 transition-colors">
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M8 3.333v9.334M3.333 8h9.334" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/></svg>
          Novo Relatório
        </button>
      </div>

      {reports.length === 0 ? (
        <div className="rounded-xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900 p-12 text-center">
          <p className="text-sm text-gray-500 dark:text-gray-400">Nenhum relatório agendado.</p>
        </div>
      ) : (
        <div className="space-y-3">
          {reports.map((report) => (
            <div key={report.id} className="rounded-xl border border-gray-200 bg-white p-4 dark:border-gray-800 dark:bg-gray-900">
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-sm font-medium text-gray-900 dark:text-white">{report.name}</p>
                  <div className="flex items-center gap-3 mt-1">
                    <span className="text-xs text-gray-500 dark:text-gray-400">{report.type}</span>
                    <span className="text-xs text-gray-500 dark:text-gray-400">{report.format}</span>
                    <span className="text-xs text-gray-500 dark:text-gray-400">{report.frequency}</span>
                    {report.lastSentAt && <span className="text-xs text-gray-400">Último: {new Intl.DateTimeFormat("pt-BR").format(new Date(report.lastSentAt))}</span>}
                  </div>
                </div>
                <div className="flex items-center gap-2">
                  <button onClick={() => handleToggle(report.id, report.enabled, report.name)} className={`relative inline-flex h-5 w-9 items-center rounded-full transition-colors ${report.enabled ? "bg-brand-500" : "bg-gray-300 dark:bg-gray-700"}`}>
                    <span className={`inline-block h-3.5 w-3.5 rounded-full bg-white transition-transform ${report.enabled ? "translate-x-4.5" : "translate-x-0.5"}`} />
                  </button>
                  <DeleteButton onClick={() => handleDelete(report.id, report.name)} ariaLabel={`Remover relatório ${report.name}`} />
                </div>
              </div>
            </div>
          ))}
        </div>
      )}

      {showForm && (
        <div className="fixed inset-0 z-[100000] flex items-center justify-center bg-gray-400/5 backdrop-blur-[3px] modal-backdrop-in">
          <div className="w-full max-w-md rounded-xl bg-white p-6 dark:bg-gray-900 shadow-xl modal-content-in">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-4">Novo Relatório Agendado</h3>
            {formError && <div className="mb-4 p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{formError}</div>}
            <form onSubmit={handleCreate} className="space-y-4">
              <Input
                label="Nome"
                type="text"
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                placeholder="Ex: Relatório semanal de vendas"
                required
              />
              <div>
                <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Tipo</label>
                <Select value={formData.type} onChange={(v) => setFormData({ ...formData, type: v })} options={typeOptions} />
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Formato</label>
                  <Select value={formData.format} onChange={(v) => setFormData({ ...formData, format: v })} options={formatOptions} />
                </div>
                <div>
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Frequência</label>
                  <Select value={formData.frequency} onChange={(v) => setFormData({ ...formData, frequency: v })} options={frequencyOptions} />
                </div>
              </div>
              <div className="flex justify-end gap-3 pt-2">
                <button type="button" onClick={() => setShowForm(false)} className="rounded-lg px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800">Cancelar</button>
                <button type="submit" disabled={formLoading} className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50">{formLoading ? "Criando..." : "Criar Relatório"}</button>
              </div>
            </form>
          </div>
        </div>
      )}

      <ConfirmModal
        isOpen={pendingAction !== null}
        title={pendingAction?.title ?? ""}
        message={pendingAction?.message ?? ""}
        confirmLabel={pendingAction?.confirmLabel}
        variant="danger"
        requireCode
        isLoading={pendingRunning}
        onCancel={() => { if (!pendingRunning) setPendingAction(null); }}
        onConfirm={runPending}
      />
    </div>
  );
}
