"use client";
import React, { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { DataTable, Column } from "@/components/ui/DataTable";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { IdDisplay } from "@/components/ui/IdDisplay";
import { receiptsService, type Receipt } from "@/services/receipts.service";
import { PageHeader } from "@/components/ui/PageHeader";
import { receiptTypeLabel } from "@/lib/formatters/enums";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" }).format(new Date(dateStr));
}

const columns: Column<Receipt>[] = [
  {
    key: "type",
    label: "Tipo",
    render: (item) => <span className="text-gray-900 dark:text-white">{receiptTypeLabel(item.type)}</span>,
  },
  {
    key: "transactionId",
    label: "Referência",
    render: (item) => {
      const ref = item.transactionId || item.payoutId;
      return ref
        ? <IdDisplay id={ref} />
        : <span className="text-xs italic text-gray-400 dark:text-gray-500">—</span>;
    },
  },
  {
    key: "amount",
    label: "Valor",
    render: (item) => <span className="font-medium text-gray-900 dark:text-white">{formatCurrency(item.amount)}</span>,
  },
  {
    key: "status",
    label: "Status",
    render: (item) => <StatusBadge status={item.status} kind="receipt" />,
  },
  {
    key: "createdAt",
    label: "Gerado em",
    render: (item) => <span className="text-gray-600 dark:text-gray-400 text-xs">{formatDate(item.createdAt)}</span>,
  },
];

export default function ReceiptsPage() {
  const [page, setPage] = useState(1);
  const pageSize = 20;

  // Backend `/receipts/seller/{id}` returns the full array; paginação é client-side.
  // Quando volume crescer, mover paging pro backend.
  const { data: allItems, isLoading, error: queryError } = useQuery({
    queryKey: ["receipts", "list-mine"],
    queryFn: () => receiptsService.listMine(),
  });
  const data = allItems ?? [];
  const error = queryError instanceof Error ? queryError.message : queryError ? "Não foi possível carregar os recibos." : null;

  const start = (page - 1) * pageSize;
  const pageItems = data.slice(start, start + pageSize);

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Recibos"
        subtitle="Comprovantes de pagamentos, reembolsos e saques do seu seller."
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
            <polyline points="14 2 14 8 20 8" />
            <line x1="8" y1="13" x2="16" y2="13" />
            <line x1="8" y1="17" x2="13" y2="17" />
          </svg>
        }
      />

      {error && (
        <div className="rounded-lg border border-error-200 bg-error-50 px-4 py-3 text-sm text-error-700 dark:border-error-500/30 dark:bg-error-500/10 dark:text-error-300">
          {error}
        </div>
      )}

      <DataTable<Receipt>
        columns={columns}
        data={pageItems}
        page={page}
        pageSize={pageSize}
        totalCount={data.length}
        onPageChange={setPage}
        isLoading={isLoading}
        emptyMessage="Nenhum recibo disponível ainda. Eles serão gerados automaticamente após pagamentos, reembolsos e saques."
      />
    </div>
  );
}
