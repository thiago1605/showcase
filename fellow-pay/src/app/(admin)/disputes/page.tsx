"use client";
import React, { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { DataTable, Column } from "@/components/ui/DataTable";
import { Select } from "@/components/ui/Select";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { disputesService, Dispute } from "@/services/disputes.service";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  if (!dateStr) return "\u2014";
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" }).format(new Date(dateStr));
}

const columns: Column<Dispute>[] = [
  {
    key: "transactionId",
    label: "Transação",
    render: (item) => <IdDisplay id={item.transactionId} />,
  },
  {
    key: "amount",
    label: "Valor",
    render: (item) => <span className="font-medium text-gray-900 dark:text-white">{formatCurrency(item.amount)}</span>,
  },
  {
    key: "reason",
    label: "Motivo",
    render: (item) => <span className="text-gray-700 dark:text-gray-300">{item.reason}</span>,
  },
  {
    key: "status",
    label: "Status",
    render: (item) => <StatusBadge status={item.status} kind="dispute" />,
  },
  {
    key: "deadline",
    label: "Prazo",
    render: (item) => <span className="text-gray-600 dark:text-gray-400 text-xs">{formatDate(item.deadline)}</span>,
  },
  {
    key: "createdAt",
    label: "Aberta em",
    render: (item) => <span className="text-gray-600 dark:text-gray-400 text-xs">{formatDate(item.createdAt)}</span>,
  },
];

const statusOptions = [
  { value: "", label: "Todos os status" },
  { value: "OPEN", label: "Aberta" },
  { value: "WON", label: "Ganha" },
  { value: "LOST", label: "Perdida" },
];

export default function DisputesPage() {
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState("");
  const pageSize = 20;

  const { data: listResult, isLoading } = useQuery({
    queryKey: ["disputes", { page, statusFilter }],
    queryFn: () => disputesService.list({ page, pageSize, status: statusFilter || undefined }),
  });
  const data = listResult?.items ?? [];
  const totalCount = listResult?.totalCount ?? 0;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold text-gray-900 dark:text-white">Disputas</h1>
        <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">Contestações abertas por clientes junto às operadoras.</p>
      </div>

      <div className="flex items-center gap-3">
        <Select
          value={statusFilter}
          onChange={(v) => { setStatusFilter(v); setPage(1); }}
          options={statusOptions}
          className="w-52"
          ariaLabel="Filtrar por status"
        />
      </div>

      <DataTable<Dispute>
        columns={columns}
        data={data}
        page={page}
        pageSize={pageSize}
        totalCount={totalCount}
        onPageChange={setPage}
        isLoading={isLoading}
        emptyMessage="Nenhuma disputa aberta. Disputas são criadas quando um cliente contesta uma cobrança."
      />
    </div>
  );
}
