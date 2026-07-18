"use client";
import React, { useState } from "react";

export interface Column<T> {
  key: string;
  label: string;
  render?: (item: T) => React.ReactNode;
  className?: string;
  sortable?: boolean;
}

interface DataTableProps<T> {
  columns: Column<T>[];
  data: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  onPageChange: (page: number) => void;
  isLoading?: boolean;
  emptyMessage?: string;
  onSort?: (key: string, direction: "asc" | "desc") => void;
  /**
   * Quando definido, a row inteira fica clicável (cursor-pointer) e dispara
   * essa callback. Padrão de marketplace de transações: o seller espera
   * clicar em qualquer parte da linha pra abrir o detalhe, não só num link
   * "Ver" microscópico no fim. Mantemos compatibilidade — tabelas sem
   * onRowClick continuam não-clicáveis e sem cursor.
   */
  onRowClick?: (item: T) => void;
}

export function DataTable<T extends object>({
  columns,
  data,
  page,
  pageSize,
  totalCount,
  onPageChange,
  isLoading = false,
  emptyMessage = "Nenhum registro encontrado.",
  onSort,
  onRowClick,
}: DataTableProps<T>) {
  const totalPages = Math.ceil(totalCount / pageSize);
  const [sortKey, setSortKey] = useState<string | null>(null);
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");

  const handleSort = (key: string) => {
    const newDir = sortKey === key && sortDir === "asc" ? "desc" : "asc";
    setSortKey(key);
    setSortDir(newDir);
    onSort?.(key, newDir);
  };

  if (isLoading) {
    // Skeleton: 5 linhas com células no shape das colunas, animadas via pulse.
    // Mantém o cabeçalho real pra evitar layout shift quando os dados chegam.
    const SKELETON_ROWS = 5;
    return (
      <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900" role="status" aria-live="polite" aria-label="Carregando">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-100 dark:border-gray-800">
                {columns.map((col) => (
                  <th
                    key={col.key}
                    className={`px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider ${col.className || ""}`}
                  >
                    {col.label}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {Array.from({ length: SKELETON_ROWS }).map((_, rowIdx) => (
                <tr key={rowIdx} className="border-b border-gray-100 dark:border-gray-800 last:border-0">
                  {columns.map((col, colIdx) => (
                    <td key={col.key} className="px-5 py-4">
                      <div
                        className="h-4 animate-pulse rounded bg-gray-200 dark:bg-gray-800"
                        style={{ width: `${50 + ((rowIdx + colIdx) % 3) * 15}%` }}
                        aria-hidden="true"
                      />
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    );
  }

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900">
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-gray-100 dark:border-gray-800">
              {columns.map((col) => (
                <th
                  key={col.key}
                  className={`px-5 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider ${col.sortable ? "cursor-pointer select-none hover:text-gray-700 dark:hover:text-gray-200" : ""} ${col.className || ""}`}
                  onClick={col.sortable ? () => handleSort(col.key) : undefined}
                >
                  <span className="inline-flex items-center gap-1">
                    {col.label}
                    {col.sortable && sortKey === col.key && (
                      <svg width="12" height="12" viewBox="0 0 12 12" fill="none" className="text-brand-500">
                        <path d={sortDir === "asc" ? "M6 3L9 7H3L6 3Z" : "M6 9L3 5H9L6 9Z"} fill="currentColor" />
                      </svg>
                    )}
                  </span>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {data.length === 0 ? (
              <tr>
                <td colSpan={columns.length} className="px-5 py-12 text-center text-sm text-gray-500 dark:text-gray-400">
                  {emptyMessage}
                </td>
              </tr>
            ) : (
              data.map((item, i) => (
                <tr
                  key={i}
                  className={`border-b border-gray-50 last:border-0 dark:border-gray-800/50 hover:bg-gray-50/70 dark:hover:bg-white/[0.03] transition-colors ${onRowClick ? "cursor-pointer" : ""}`}
                  onClick={onRowClick ? () => onRowClick(item) : undefined}
                >
                  {columns.map((col) => (
                    <td key={col.key} className={`px-5 py-3 ${col.className || ""}`}>
                      {col.render ? col.render(item) : String((item as Record<string, unknown>)[col.key] ?? "")}
                    </td>
                  ))}
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {totalPages > 1 && (
        <div className="flex items-center justify-between px-5 py-3 border-t border-gray-100 dark:border-gray-800">
          <p className="text-xs text-gray-500 dark:text-gray-400">
            {((page - 1) * pageSize) + 1}&ndash;{Math.min(page * pageSize, totalCount)} de {totalCount}
          </p>
          <div className="flex items-center gap-1">
            <button
              onClick={() => onPageChange(page - 1)}
              disabled={page <= 1}
              className="px-2.5 py-1.5 rounded-md text-xs font-medium text-gray-600 hover:bg-gray-100 disabled:opacity-40 disabled:cursor-not-allowed dark:text-gray-400 dark:hover:bg-gray-800"
            >
              Anterior
            </button>
            {Array.from({ length: Math.min(totalPages, 5) }, (_, i) => {
              const pageNum = i + 1;
              return (
                <button
                  key={pageNum}
                  onClick={() => onPageChange(pageNum)}
                  className={`px-2.5 py-1.5 rounded-md text-xs font-medium ${
                    page === pageNum
                      ? "bg-brand-500 text-white"
                      : "text-gray-600 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
                  }`}
                >
                  {pageNum}
                </button>
              );
            })}
            <button
              onClick={() => onPageChange(page + 1)}
              disabled={page >= totalPages}
              className="px-2.5 py-1.5 rounded-md text-xs font-medium text-gray-600 hover:bg-gray-100 disabled:opacity-40 disabled:cursor-not-allowed dark:text-gray-400 dark:hover:bg-gray-800"
            >
              Próximo
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
