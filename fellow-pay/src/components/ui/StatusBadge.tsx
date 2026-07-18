"use client";
import React from "react";
import {
  transactionStatusKey,
  payoutStatusKey,
  subscriptionStatusKey,
  refundIntentStatusKey,
  disputeStatusKey,
  deliveryStatusKey,
  receiptStatusKey,
} from "@/lib/formatters/enums";

const statusConfig: Record<string, { label: string; className: string }> = {
  CAPTURED: { label: "Aprovada", className: "bg-success-50 text-success-700 dark:bg-success-500/10 dark:text-success-400" },
  AUTHORIZED: { label: "Autorizada", className: "bg-success-50 text-success-700 dark:bg-success-500/10 dark:text-success-400" },
  PROCESSING: { label: "Processando", className: "bg-warning-50 text-warning-700 dark:bg-warning-500/10 dark:text-warning-400" },
  CREATED: { label: "Criada", className: "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300" },
  DECLINED: { label: "Recusada", className: "bg-error-50 text-error-700 dark:bg-error-500/10 dark:text-error-400" },
  FAILED: { label: "Falhou", className: "bg-error-50 text-error-700 dark:bg-error-500/10 dark:text-error-400" },
  REFUNDED: { label: "Reembolsada", className: "bg-blue-light-50 text-blue-light-700 dark:bg-blue-light-500/10 dark:text-blue-light-400" },
  VOIDED: { label: "Cancelada", className: "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400" },
  CHARGEBACKERROR: { label: "Chargeback", className: "bg-error-50 text-error-700 dark:bg-error-500/10 dark:text-error-400" },
  PENDING: { label: "Pendente", className: "bg-warning-50 text-warning-700 dark:bg-warning-500/10 dark:text-warning-400" },
  PAID: { label: "Pago", className: "bg-success-50 text-success-700 dark:bg-success-500/10 dark:text-success-400" },
  CANCELED: { label: "Cancelado", className: "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400" },
  ACTIVE: { label: "Ativa", className: "bg-success-50 text-success-700 dark:bg-success-500/10 dark:text-success-400" },
  PAUSED: { label: "Pausada", className: "bg-warning-50 text-warning-700 dark:bg-warning-500/10 dark:text-warning-400" },
  EXPIRED: { label: "Expirada", className: "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400" },
  OPEN: { label: "Aberta", className: "bg-error-50 text-error-700 dark:bg-error-500/10 dark:text-error-400" },
  WON: { label: "Ganha", className: "bg-success-50 text-success-700 dark:bg-success-500/10 dark:text-success-400" },
  LOST: { label: "Perdida", className: "bg-error-50 text-error-700 dark:bg-error-500/10 dark:text-error-400" },
  COMPLETED: { label: "Completo", className: "bg-success-50 text-success-700 dark:bg-success-500/10 dark:text-success-400" },
  SUCCEEDED: { label: "Sucesso", className: "bg-success-50 text-success-700 dark:bg-success-500/10 dark:text-success-400" },
  PENDING_RETRY: { label: "Retry", className: "bg-warning-50 text-warning-700 dark:bg-warning-500/10 dark:text-warning-400" },
};

// Backend may serialize a status as either a string ("CAPTURED") or an integer (3, the
// enum index). The `kind` prop tells the badge which enum family the value belongs to so
// we can resolve a numeric value to its canonical key. Falls back to treating the value
// as a string if `kind` is omitted.
type StatusKind =
  | "transaction"
  | "payout"
  | "subscription"
  | "refund"
  | "dispute"
  | "delivery"
  | "receipt";

interface StatusBadgeProps {
  status: string | number | null | undefined;
  kind?: StatusKind;
}

const RESOLVERS: Record<StatusKind, (v: string | number | null | undefined) => string | null> = {
  transaction: transactionStatusKey,
  payout: payoutStatusKey,
  subscription: subscriptionStatusKey,
  refund: refundIntentStatusKey,
  dispute: disputeStatusKey,
  delivery: deliveryStatusKey,
  receipt: receiptStatusKey,
};

export function StatusBadge({ status, kind }: StatusBadgeProps) {
  const key = kind
    ? RESOLVERS[kind](status)
    : typeof status === "string"
    ? status
    : null;

  const fallbackLabel = key ?? (status == null ? "—" : String(status));
  const config = (key && statusConfig[key]) || {
    label: fallbackLabel,
    className: "bg-gray-100 text-gray-600 dark:bg-gray-800 dark:text-gray-400",
  };

  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${config.className}`}>
      {config.label}
    </span>
  );
}
