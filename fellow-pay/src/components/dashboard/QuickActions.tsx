"use client";
import React from "react";
import Link from "next/link";

const actions = [
  { label: "Criar link de pagamento", href: "/payment-links", description: "Gere um link de cobrança" },
  { label: "Simular Split", href: "/split-simulator", description: "Preveja a divisão de valores" },
  { label: "Ver Recibos", href: "/receipts", description: "Acesse seus comprovantes" },
  { label: "Configurar Webhooks", href: "/webhooks", description: "Receba notificações" },
];

export function QuickActions() {
  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900">
      <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <h3 className="text-sm font-semibold text-gray-900 dark:text-white">
          Ações Rápidas
        </h3>
      </div>
      <div className="grid grid-cols-2 gap-3 p-5 sm:grid-cols-4">
        {actions.map((action) => (
          <Link
            key={action.href}
            href={action.href}
            className="rounded-lg border border-gray-200 p-3 hover:border-brand-200 hover:bg-brand-50/50 transition-colors dark:border-gray-800 dark:hover:border-brand-800 dark:hover:bg-brand-500/5"
          >
            <p className="text-sm font-medium text-gray-800 dark:text-gray-200">
              {action.label}
            </p>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              {action.description}
            </p>
          </Link>
        ))}
      </div>
    </div>
  );
}
