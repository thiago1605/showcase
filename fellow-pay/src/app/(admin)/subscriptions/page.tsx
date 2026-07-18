"use client";
import React, { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { DataTable, Column } from "@/components/ui/DataTable";
import { Select } from "@/components/ui/Select";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { subscriptionsService } from "@/services/subscriptions.service";
import { PageHeader, PageHeaderButton } from "@/components/ui/PageHeader";
import type { Subscription } from "@/types";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  if (!dateStr) return "\u2014";
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" }).format(new Date(dateStr));
}

const intervalLabels: Record<string, string> = {
  WEEKLY: "Semanal",
  MONTHLY: "Mensal",
  QUARTERLY: "Trimestral",
  YEARLY: "Anual",
};

const columns: Column<Subscription>[] = [
  {
    key: "customerName",
    label: "Cliente",
    render: (item) => <span className="font-medium text-gray-900 dark:text-white">{item.customerName}</span>,
  },
  {
    key: "description",
    label: "Descrição",
    render: (item) => <span className="text-gray-700 dark:text-gray-300">{item.description || "\u2014"}</span>,
  },
  {
    key: "amount",
    label: "Valor",
    render: (item) => <span className="text-gray-900 dark:text-white">{formatCurrency(item.amount)}</span>,
  },
  {
    key: "interval",
    label: "Intervalo",
    render: (item) => <span className="text-gray-700 dark:text-gray-300">{intervalLabels[item.interval] || item.interval}</span>,
  },
  {
    key: "status",
    label: "Status",
    render: (item) => <StatusBadge status={item.status} kind="subscription" />,
  },
  {
    key: "nextBillingDate",
    label: "Próxima cobrança",
    render: (item) => <span className="text-gray-600 dark:text-gray-400 text-xs">{formatDate(item.nextBillingDate)}</span>,
  },
];

const statusOptions = [
  { value: "", label: "Todos os status" },
  { value: "ACTIVE", label: "Ativa" },
  { value: "PAUSED", label: "Pausada" },
  { value: "CANCELED", label: "Cancelada" },
  { value: "EXPIRED", label: "Expirada" },
];

export default function SubscriptionsPage() {
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState("");
  const pageSize = 20;

  const { data: listResult, isLoading } = useQuery({
    queryKey: ["subscriptions", { page, statusFilter }],
    queryFn: () => subscriptionsService.list({ page, pageSize, status: statusFilter || undefined }),
  });
  const data = listResult?.items ?? [];
  const totalCount = listResult?.totalCount ?? 0;

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Assinaturas"
        subtitle="Gerencie cobranças recorrentes dos seus clientes."
        actions={<PageHeaderButton href="/subscriptions/new">+ Nova assinatura</PageHeaderButton>}
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <polyline points="23 4 23 10 17 10" />
            <polyline points="1 20 1 14 7 14" />
            <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15" />
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

      <DataTable<Subscription>
        columns={columns}
        data={data}
        page={page}
        pageSize={pageSize}
        totalCount={totalCount}
        onPageChange={setPage}
        isLoading={isLoading}
        emptyMessage="Nenhuma assinatura encontrada."
      />
    </div>
  );
}
