"use client";
import React, { useState } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { DataTable, Column } from "@/components/ui/DataTable";
import { Select } from "@/components/ui/Select";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { PageHeader } from "@/components/ui/PageHeader";
import { refundsService, RefundIntent } from "@/services/refunds.service";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" }).format(new Date(dateStr));
}

const columns: Column<RefundIntent>[] = [
  {
    key: "transactionId",
    label: "Transação",
    render: (item) => (
      <Link href={`/transactions/${item.transactionId}`} className="text-brand-500 hover:text-brand-600">
        <IdDisplay id={item.transactionId} />
      </Link>
    ),
  },
  {
    key: "amount",
    label: "Valor",
    render: (item) => <span className="font-medium text-gray-900 dark:text-white">{formatCurrency(item.amount)}</span>,
  },
  {
    key: "reason",
    label: "Motivo",
    render: (item) => <span className="text-gray-700 dark:text-gray-300">{item.reason || "\u2014"}</span>,
  },
  {
    key: "status",
    label: "Status",
    render: (item) => <StatusBadge status={item.status} kind="refund" />,
  },
  {
    key: "createdAt",
    label: "Data",
    render: (item) => <span className="text-gray-600 dark:text-gray-400 text-xs">{formatDate(item.createdAt)}</span>,
  },
];

const statusOptions = [
  { value: "", label: "Todos os status" },
  { value: "PENDING", label: "Pendente" },
  { value: "PROCESSING", label: "Processando" },
  { value: "COMPLETED", label: "Completo" },
  { value: "FAILED", label: "Falhou" },
];

export default function RefundsPage() {
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState("");
  const pageSize = 20;

  const { data: listResult, isLoading } = useQuery({
    queryKey: ["refunds", { page, statusFilter }],
    queryFn: () => refundsService.list({ page, pageSize, status: statusFilter || undefined }),
  });
  const data = listResult?.items ?? [];
  const totalCount = listResult?.totalCount ?? 0;

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Reembolsos"
        subtitle="Reembolsos processados nas suas transações."
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="M3 7v6h6" />
            <path d="M21 17a9 9 0 0 0-15-6.7L3 13" />
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

      <DataTable<RefundIntent>
        columns={columns}
        data={data}
        page={page}
        pageSize={pageSize}
        totalCount={totalCount}
        onPageChange={setPage}
        isLoading={isLoading}
        emptyMessage="Nenhum reembolso processado. Reembolsos podem ser iniciados a partir dos detalhes de uma transação."
      />
    </div>
  );
}
