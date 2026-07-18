"use client";
import React, { useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { DataTable, Column } from "@/components/ui/DataTable";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { transactionsService } from "@/services/transactions.service";
import { exportService } from "@/services/export.service";
import { PaymentMethodBadge } from "@/components/ui/PaymentMethodBadge";
import { Select } from "@/components/ui/Select";
import { PageHeader, PageHeaderButton } from "@/components/ui/PageHeader";
import type { Transaction, TransactionStatus, PaymentType } from "@/types";

function formatCurrency(value: number) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(value);
}

function formatDate(dateStr: string) {
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit" }).format(new Date(dateStr));
}

/**
 * Converte uma data YYYY-MM-DD (interpretada no fuso local do usuário) pra ISO
 * datetime cobrindo o início ou o fim do dia. Necessário pra alinhar com o que
 * a dashboard envia — caso contrário o backend recebe "2026-05-11" como meia-noite
 * UTC, que em BRT é 21h do dia anterior, perdendo transações do dia escolhido.
 */
function dateOnlyToIsoRange(dateOnly: string, edge: "start" | "end"): string | undefined {
  if (!dateOnly) return undefined;
  const parts = dateOnly.split("-").map(Number);
  if (parts.length !== 3 || parts.some((n) => Number.isNaN(n))) return undefined;
  const [y, m, d] = parts;
  const local = edge === "end"
    ? new Date(y, m - 1, d, 23, 59, 59, 999)
    : new Date(y, m - 1, d, 0, 0, 0, 0);
  return local.toISOString();
}

const columns: Column<Transaction>[] = [
  {
    key: "payer",
    label: "Cliente",
    render: (item) => (
      item.payerName ? (
        <div>
          <p className="font-medium text-gray-900 dark:text-white">{item.payerName}</p>
          {item.payerEmail && (
            <p className="text-xs text-gray-500 dark:text-gray-400">{item.payerEmail}</p>
          )}
        </div>
      ) : (
        <span className="text-xs italic text-gray-400 dark:text-gray-500">Cliente não informado</span>
      )
    ),
  },
  {
    key: "amount",
    label: "Valor",
    render: (item) => (
      <span className="font-medium text-gray-900 dark:text-white">{formatCurrency(item.amount)}</span>
    ),
  },
  {
    key: "paymentType",
    label: "Método",
    render: (item) => <PaymentMethodBadge type={item.paymentType} />,
  },
  {
    key: "status",
    label: "Status",
    render: (item) => <StatusBadge status={item.status} kind="transaction" />,
  },
  {
    key: "createdAt",
    label: "Data",
    render: (item) => (
      <span className="text-gray-600 dark:text-gray-400 text-xs">{formatDate(item.createdAt)}</span>
    ),
  },
  {
    // Chevron como affordance visual de "abre detalhe" — a row inteira é
    // clicável via onRowClick, isso é só a "seta" pra indicar interatividade.
    // Substitui o link "Ver" pequeno que era a única pista antes.
    key: "actions",
    label: "",
    className: "w-10 text-right",
    render: () => (
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" className="inline-block text-gray-400 dark:text-gray-600" aria-hidden="true">
        <polyline points="9 18 15 12 9 6" />
      </svg>
    ),
  },
];

const statusOptions: { value: string; label: string }[] = [
  { value: "", label: "Todos os status" },
  { value: "CAPTURED", label: "Aprovada" },
  { value: "AUTHORIZED", label: "Autorizada" },
  { value: "PROCESSING", label: "Processando" },
  { value: "DECLINED", label: "Recusada" },
  { value: "FAILED", label: "Falhou" },
  { value: "REFUNDED", label: "Reembolsada" },
  { value: "VOIDED", label: "Cancelada" },
];

const paymentTypeOptions: { value: string; label: string }[] = [
  { value: "", label: "Todos os métodos" },
  { value: "CREDIT_CARD", label: "Cartão de Crédito" },
  { value: "DEBIT_CARD", label: "Cartão de Débito" },
  { value: "PIX", label: "PIX" },
  { value: "BOLETO", label: "Boleto" },
];

export default function TransactionsPage() {
  // Drill-down da dashboard: ?status=CAPTURED&from=...&to=... pré-popula filtros.
  // Busca da global header: ?q=foo — pré-popula o input de busca livre.
  const router = useRouter();
  const searchParams = useSearchParams();
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState(() => searchParams.get("status") ?? "");
  const [paymentTypeFilter, setPaymentTypeFilter] = useState(() => searchParams.get("paymentType") ?? "");
  const [dateFrom, setDateFrom] = useState(() => searchParams.get("from") ?? "");
  const [dateTo, setDateTo] = useState(() => searchParams.get("to") ?? "");
  const [searchInput, setSearchInput] = useState(() => searchParams.get("q") ?? "");
  // Debounce simples — só dispara a query depois que o user para de digitar 350ms.
  const [debouncedSearch, setDebouncedSearch] = useState(() => searchParams.get("q") ?? "");
  const pageSize = 20;

  React.useEffect(() => {
    const id = setTimeout(() => {
      setDebouncedSearch(searchInput.trim());
      setPage(1);
    }, 350);
    return () => clearTimeout(id);
  }, [searchInput]);

  // Re-sincroniza com URL quando o usuário navega (ex: search global do header
  // muda o ?q= sem desmontar a página).
  React.useEffect(() => {
    const urlQ = searchParams.get("q") ?? "";
    setSearchInput(urlQ);
    setDebouncedSearch(urlQ);
  }, [searchParams]);

  // Os inputs date entregam YYYY-MM-DD em horário LOCAL. O backend filtra
  // `CreatedAt >= from && CreatedAt <= to` interpretando ISO datetime — passar
  // só "2026-05-11" vira meia-noite UTC, que em BRT é 21h do dia anterior,
  // perdendo a porção do dia. Expandimos pra início/fim do dia local antes de
  // enviar pra API.
  const fromIso = React.useMemo(() => dateOnlyToIsoRange(dateFrom, "start"), [dateFrom]);
  const toIso = React.useMemo(() => dateOnlyToIsoRange(dateTo, "end"), [dateTo]);

  // Cache de listagem por filtro: trocar de página/voltar não refaz request
  // dentro do staleTime. Erros viram lista vazia + mensagem do DataTable.
  const { data: listResult, isLoading } = useQuery({
    queryKey: ["transactions", { page, statusFilter, paymentTypeFilter, fromIso, toIso, debouncedSearch }],
    queryFn: () =>
      transactionsService.list({
        page,
        pageSize,
        status: statusFilter || undefined,
        paymentType: paymentTypeFilter || undefined,
        from: fromIso,
        to: toIso,
        q: debouncedSearch || undefined,
      }),
  });
  const data = listResult?.items ?? [];
  const totalCount = listResult?.totalCount ?? 0;

  return (
    <div className="space-y-6">
      <PageHeader
        size="hero"
        title="Transações"
        subtitle="Acompanhe todas as vendas processadas na sua conta."
        actions={
          <>
            <PageHeaderButton
              variant="ghost"
              onClick={async () => {
                try {
                  const blob = await exportService.exportTransactions("csv", { status: statusFilter, from: fromIso, to: toIso });
                  exportService.downloadBlob(blob, "transacoes.csv");
                } catch { /* silently fail */ }
              }}
            >
              CSV
            </PageHeaderButton>
            <PageHeaderButton
              variant="ghost"
              onClick={async () => {
                try {
                  const blob = await exportService.exportTransactions("pdf", { status: statusFilter, from: fromIso, to: toIso });
                  exportService.downloadBlob(blob, "transacoes.pdf");
                } catch { /* silently fail */ }
              }}
            >
              PDF
            </PageHeaderButton>
          </>
        }
        decorIcon={
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
            <path d="M16 3l4 4-4 4M20 7H4M8 21l-4-4 4-4M4 17h16" />
          </svg>
        }
      />

      {/* Filtros — COMPACTOS (h-11) mas com a MESMA paleta visual do Input
          compartilhado: bg-gray-50, shadow-sm, border-gray-200/80, focus
          brand-500. Filtros não precisam do form-factor stacked (que é pra
          forms), só precisam compartilhar a identidade visual pra parecerem
          parte do mesmo design system. */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="relative flex-1 min-w-[260px] max-w-md">
          <span className="absolute left-3.5 top-1/2 -translate-y-1/2 text-gray-400 dark:text-gray-500 pointer-events-none">
            <svg width="18" height="18" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
              <path fillRule="evenodd" d="M3.04 9.37a6.33 6.33 0 1 1 12.67 0 6.33 6.33 0 0 1-12.67 0Zm6.34-7.83a7.83 7.83 0 0 0-4.98 13.88l-2.82 2.82a.75.75 0 1 0 1.06 1.06l2.82-2.82a7.83 7.83 0 1 0 3.92-14.94Z" clipRule="evenodd"/>
            </svg>
          </span>
          <input
            type="text"
            value={searchInput}
            onChange={(e) => setSearchInput(e.target.value)}
            placeholder="Buscar por cliente, e-mail ou ID..."
            className="w-full h-11 rounded-lg bg-white dark:bg-gray-900/60 pl-10 pr-10 text-sm font-light text-gray-900 dark:text-white border border-gray-200/80 dark:border-gray-800 placeholder:text-gray-400 dark:placeholder:text-gray-500 focus:border-brand-500 dark:focus:border-brand-500 focus:outline-none transition-colors"
          />
          {searchInput && (
            <button
              type="button"
              onClick={() => setSearchInput("")}
              aria-label="Limpar busca"
              className="absolute right-2.5 top-1/2 -translate-y-1/2 inline-flex items-center justify-center w-6 h-6 rounded text-gray-400 hover:text-gray-700 dark:hover:text-gray-200"
            >
              <svg width="14" height="14" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
                <path fillRule="evenodd" d="M4.22 4.22a.75.75 0 0 1 1.06 0L10 8.94l4.72-4.72a.75.75 0 1 1 1.06 1.06L11.06 10l4.72 4.72a.75.75 0 1 1-1.06 1.06L10 11.06l-4.72 4.72a.75.75 0 0 1-1.06-1.06L8.94 10 4.22 5.28a.75.75 0 0 1 0-1.06Z" clipRule="evenodd"/>
              </svg>
            </button>
          )}
        </div>
        <Select
          value={statusFilter}
          onChange={(v) => { setStatusFilter(v); setPage(1); }}
          options={statusOptions}
          className="w-48 h-11"
          ariaLabel="Filtrar por status"
        />
        <Select
          value={paymentTypeFilter}
          onChange={(v) => { setPaymentTypeFilter(v); setPage(1); }}
          options={paymentTypeOptions}
          className="w-48 h-11"
          ariaLabel="Filtrar por método"
        />
        <label className="inline-flex items-center gap-2 h-11 rounded-lg bg-white dark:bg-gray-900/60 px-3.5 border border-gray-200/80 dark:border-gray-800 focus-within:border-brand-500 dark:focus-within:border-brand-500 transition-colors">
          <span className="text-xs text-gray-500 dark:text-gray-400">De</span>
          <input
            type="date"
            value={dateFrom}
            onChange={(e) => { setDateFrom(e.target.value); setPage(1); }}
            className="bg-transparent border-0 p-0 text-sm font-light text-gray-900 dark:text-white focus:outline-none focus:ring-0"
          />
        </label>
        <label className="inline-flex items-center gap-2 h-11 rounded-lg bg-white dark:bg-gray-900/60 px-3.5 border border-gray-200/80 dark:border-gray-800 focus-within:border-brand-500 dark:focus-within:border-brand-500 transition-colors">
          <span className="text-xs text-gray-500 dark:text-gray-400">Até</span>
          <input
            type="date"
            value={dateTo}
            onChange={(e) => { setDateTo(e.target.value); setPage(1); }}
            className="bg-transparent border-0 p-0 text-sm font-light text-gray-900 dark:text-white focus:outline-none focus:ring-0"
          />
        </label>
        {(statusFilter || paymentTypeFilter || dateFrom || dateTo) && (
          <button
            onClick={() => { setStatusFilter(""); setPaymentTypeFilter(""); setDateFrom(""); setDateTo(""); setPage(1); }}
            className="inline-flex items-center h-11 rounded-lg px-3 text-sm font-medium text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800 transition-colors"
          >
            Limpar filtros
          </button>
        )}
      </div>

      <DataTable<Transaction>
        columns={columns}
        data={data}
        page={page}
        pageSize={pageSize}
        totalCount={totalCount}
        onPageChange={setPage}
        isLoading={isLoading}
        emptyMessage="Nenhuma transação encontrada. Suas vendas aparecerão aqui conforme forem processadas."
        onRowClick={(item) => router.push(`/transactions/${item.id}`)}
      />
    </div>
  );
}
