"use client";
import React, { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { DataTable, Column } from "@/components/ui/DataTable";
import { customersService } from "@/services/customers.service";
import type { Customer } from "@/types";

function formatDate(dateStr: string) {
  return new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" }).format(new Date(dateStr));
}

const columns: Column<Customer>[] = [
  {
    key: "name",
    label: "Nome",
    render: (item) => (
      <span className="font-medium text-gray-900 dark:text-white">{item.name}</span>
    ),
  },
  {
    key: "email",
    label: "Email",
    render: (item) => (
      <span className="text-gray-700 dark:text-gray-300">{item.email}</span>
    ),
  },
  {
    key: "document",
    label: "Documento",
    render: (item) => (
      <span className="text-gray-600 dark:text-gray-400 font-mono text-xs">{item.document}</span>
    ),
  },
  {
    key: "createdAt",
    label: "Cadastro",
    render: (item) => (
      <span className="text-gray-600 dark:text-gray-400 text-xs">{formatDate(item.createdAt)}</span>
    ),
  },
];

export default function CustomersPage() {
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState("");
  const [searchInput, setSearchInput] = useState("");
  const pageSize = 20;

  const { data: listResult, isLoading } = useQuery({
    queryKey: ["customers", { page, search }],
    queryFn: () => customersService.list({ page, pageSize, search: search || undefined }),
  });
  const data = listResult?.items ?? [];
  const totalCount = listResult?.totalCount ?? 0;

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault();
    setSearch(searchInput);
    setPage(1);
  };

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold text-gray-900 dark:text-white">Clientes</h1>
        <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
          Clientes que realizaram compras na sua conta.
        </p>
      </div>

      <form onSubmit={handleSearch} className="flex items-center gap-3">
        <input
          type="text"
          value={searchInput}
          onChange={(e) => setSearchInput(e.target.value)}
          placeholder="Buscar por nome, email ou documento..."
          className="flex-1 max-w-sm rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-900 dark:text-gray-300 focus:border-brand-500 focus:outline-none"
        />
        <button
          type="submit"
          className="rounded-lg bg-brand-500 px-4 py-2 text-sm font-medium text-white hover:bg-brand-600 transition-colors"
        >
          Buscar
        </button>
        {search && (
          <button
            type="button"
            onClick={() => { setSearch(""); setSearchInput(""); setPage(1); }}
            className="text-xs text-gray-500 hover:text-gray-700 dark:text-gray-400"
          >
            Limpar
          </button>
        )}
      </form>

      <DataTable<Customer>
        columns={columns}
        data={data}
        page={page}
        pageSize={pageSize}
        totalCount={totalCount}
        onPageChange={setPage}
        isLoading={isLoading}
        emptyMessage="Nenhum cliente encontrado."
      />
    </div>
  );
}
