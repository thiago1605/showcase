"use client";
import React, { useState } from "react";
import { useScrollLock } from "@/hooks/useScrollLock";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { DataTable, Column } from "@/components/ui/DataTable";
import { Select } from "@/components/ui/Select";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { payoutsService } from "@/services/payouts.service";
import Input from "@/components/form/input/InputField";
import { PageHeader, PageHeaderButton } from "@/components/ui/PageHeader";
import type { Payout } from "@/types";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  if (!dateStr) return "\u2014";
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" }).format(new Date(dateStr));
}

const columns: Column<Payout>[] = [
  {
    key: "amount",
    label: "Valor",
    render: (item) => <span className="font-medium text-gray-900 dark:text-white">{formatCurrency(item.amount)}</span>,
  },
  {
    key: "fee",
    label: "Taxa",
    render: (item) => <span className="text-gray-600 dark:text-gray-400">{formatCurrency(item.fee)}</span>,
  },
  {
    key: "status",
    label: "Status",
    render: (item) => <StatusBadge status={item.status} kind="payout" />,
  },
  {
    key: "processedAt",
    label: "Processado em",
    render: (item) => <span className="text-gray-600 dark:text-gray-400 text-xs">{formatDate(item.processedAt)}</span>,
  },
  {
    key: "createdAt",
    label: "Solicitado em",
    render: (item) => <span className="text-gray-600 dark:text-gray-400 text-xs">{formatDate(item.createdAt)}</span>,
  },
];

const statusOptions = [
  { value: "", label: "Todos os status" },
  { value: "PENDING", label: "Pendente" },
  { value: "PROCESSING", label: "Processando" },
  { value: "PAID", label: "Pago" },
  { value: "FAILED", label: "Falhou" },
  { value: "CANCELED", label: "Cancelado" },
];

export default function PayoutsPage() {
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState("");
  const [showForm, setShowForm] = useState(false);
  const [payoutAmount, setPayoutAmount] = useState("");
  const [formLoading, setFormLoading] = useState(false);
  const [formError, setFormError] = useState("");

  // Trava scroll enquanto o modal de saque estiver aberto.
  useScrollLock(showForm);
  const pageSize = 20;
  const queryClient = useQueryClient();

  const { data: listResult, isLoading } = useQuery({
    queryKey: ["payouts", { page, statusFilter }],
    queryFn: () => payoutsService.list({ page, pageSize, status: statusFilter || undefined }),
  });
  const data = listResult?.items ?? [];
  const totalCount = listResult?.totalCount ?? 0;

  const handleRequestPayout = async (e: React.FormEvent) => {
    e.preventDefault();
    setFormError("");
    setFormLoading(true);
    try {
      const amount = parseFloat(payoutAmount);
      await payoutsService.requestPayout(amount);
      setShowForm(false);
      setPayoutAmount("");
      // Invalida payouts (lista) + balance da dashboard (saque consumiu disponível).
      queryClient.invalidateQueries({ queryKey: ["payouts"] });
      queryClient.invalidateQueries({ queryKey: ["dashboard"] });
    } catch {
      setFormError("Erro ao solicitar saque. Verifique seu saldo disponível.");
    }
    setFormLoading(false);
  };

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Saques"
        subtitle="Histórico de saques realizados."
        actions={<PageHeaderButton onClick={() => setShowForm(true)}>+ Solicitar saque</PageHeaderButton>}
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <rect x="2" y="6" width="20" height="14" rx="2" />
            <path d="M16 12h4" />
            <path d="M18 10l2 2-2 2" />
          </svg>
        }
      />

      <div className="flex items-center gap-3">
        <Select
          value={statusFilter}
          onChange={(v) => { setStatusFilter(v); setPage(1); }}
          options={statusOptions}
          className="w-52"
          ariaLabel="Filtrar por status"
        />
      </div>

      <DataTable<Payout>
        columns={columns}
        data={data}
        page={page}
        pageSize={pageSize}
        totalCount={totalCount}
        onPageChange={setPage}
        isLoading={isLoading}
        emptyMessage="Nenhum saque encontrado."
      />

      {showForm && (
        <div className="fixed inset-0 z-[100000] flex items-center justify-center bg-gray-400/5 backdrop-blur-[3px] modal-backdrop-in">
          <div className="w-full max-w-sm rounded-xl bg-white p-6 dark:bg-gray-900 shadow-xl modal-content-in">
            <h3 className="text-lg font-semibold text-gray-900 dark:text-white mb-4">Solicitar Saque</h3>
            {formError && <div className="mb-4 p-3 rounded-lg bg-error-50 dark:bg-error-500/10 text-error-700 dark:text-error-400 text-sm">{formError}</div>}
            <form onSubmit={handleRequestPayout} className="space-y-4">
              <Input
                label="Valor (R$)"
                type="number"
                step={0.01}
                min="0.01"
                value={payoutAmount}
                onChange={(e) => setPayoutAmount(e.target.value)}
                placeholder="0,00"
                required
              />
              <div className="flex justify-end gap-3 pt-2">
                <button type="button" onClick={() => setShowForm(false)} className="rounded-lg px-4 py-2 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800">Cancelar</button>
                <button type="submit" disabled={formLoading} className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 disabled:opacity-50">{formLoading ? "Processando..." : "Confirmar Saque"}</button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
