"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { notificationsService } from "@/services/notifications.service";
import {
  useMarkAllNotificationsRead,
  useMarkNotificationRead,
} from "@/hooks/useNotifications";
import type { Notification, NotificationTypeCode } from "@/types";

/**
 * Página /notifications — listagem paginada cheia. Complementar ao dropdown
 * do header (que mostra só as últimas 20 e abre quando clicado). Aqui o seller
 * vê o histórico completo, pode filtrar não lidas, marcar todas, etc.
 *
 * Decisões:
 *  - Paginação simples (page/pageSize) em vez de infinite scroll — bate com
 *    o resto do app (/refunds, /transactions) e simplifica a UX.
 *  - Toggle "Só não lidas" via query param `unreadOnly`. Backend já suporta.
 *  - Click numa notification: marca lida + navega pra resourceUrl se houver.
 *  - "Marcar todas como lidas" no header da página + invalidate de queries.
 */

const TYPE_LABEL: Record<NotificationTypeCode, string> = {
  TRANSACTION_CAPTURED: "Pagamento",
  TRANSACTION_REFUNDED: "Reembolso",
  DISPUTE_OPENED: "Contestação",
  DISPUTE_RESOLVED: "Contestação",
  PAYOUT_COMPLETED: "Saque",
  PAYOUT_FAILED: "Saque",
  TIER_UPGRADED: "Nível",
  TIER_DOWNGRADED: "Nível",
  BALANCE_RELEASED: "Saldo",
  WEBHOOK_DELIVERY_FAILED: "Webhook",
  SYSTEM_ANNOUNCEMENT: "Sistema",
};

const TYPE_DOT_CLS: Record<NotificationTypeCode, string> = {
  TRANSACTION_CAPTURED: "bg-success-500",
  TRANSACTION_REFUNDED: "bg-gray-400",
  DISPUTE_OPENED: "bg-error-500",
  DISPUTE_RESOLVED: "bg-success-500",
  PAYOUT_COMPLETED: "bg-success-500",
  PAYOUT_FAILED: "bg-error-500",
  TIER_UPGRADED: "bg-brand-500",
  TIER_DOWNGRADED: "bg-warning-500",
  BALANCE_RELEASED: "bg-success-500",
  WEBHOOK_DELIVERY_FAILED: "bg-warning-500",
  SYSTEM_ANNOUNCEMENT: "bg-brand-500",
};

const PAGE_SIZE = 20;

function formatDateTime(iso: string): string {
  return new Intl.DateTimeFormat("pt-BR", {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(iso));
}

export default function NotificationsPage() {
  const [page, setPage] = useState(1);
  const [unreadOnly, setUnreadOnly] = useState(false);
  const router = useRouter();

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ["notifications", "list", { page, unreadOnly }],
    queryFn: () =>
      notificationsService.list({ page, pageSize: PAGE_SIZE, unreadOnly }),
  });

  const markRead = useMarkNotificationRead();
  const markAllRead = useMarkAllNotificationsRead();

  const items = data?.items ?? [];
  const totalCount = data?.totalCount ?? 0;
  const unreadCount = data?.unreadCount ?? 0;
  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));

  function handleClick(n: Notification) {
    if (!n.readAt) {
      markRead.mutate(n.id);
    }
    if (n.resourceUrl) {
      router.push(n.resourceUrl);
    }
  }

  function handleMarkAll() {
    if (unreadCount === 0) return;
    markAllRead.mutate();
  }

  function handleToggleUnread() {
    setUnreadOnly((v) => !v);
    setPage(1);
  }

  return (
    <div className="space-y-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-xl font-semibold text-gray-900 dark:text-white">
            Notificações
          </h1>
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
            Histórico de avisos sobre sua conta — capturas, reembolsos,
            contestações, saques e mudanças de nível.
          </p>
        </div>
        {unreadCount > 0 && (
          <button
            onClick={handleMarkAll}
            disabled={markAllRead.isPending}
            className="h-9 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-50 transition-colors whitespace-nowrap"
          >
            Marcar todas como lidas{" "}
            <span className="opacity-70">({unreadCount})</span>
          </button>
        )}
      </div>

      <div className="flex items-center gap-3">
        <button
          onClick={handleToggleUnread}
          className={`h-9 rounded-lg px-4 text-sm font-medium transition-colors ${
            unreadOnly
              ? "bg-brand-500 text-white"
              : "border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800"
          }`}
        >
          Só não lidas
        </button>
        <span className="text-sm text-gray-500 dark:text-gray-400 tabular-nums">
          {totalCount} {totalCount === 1 ? "notificação" : "notificações"}
        </span>
      </div>

      {isLoading ? (
        <ListSkeleton />
      ) : isError ? (
        <ErrorBox message={error instanceof Error ? error.message : "Não foi possível carregar."} />
      ) : items.length === 0 ? (
        <EmptyBox unreadOnly={unreadOnly} />
      ) : (
        <>
          <ul className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800 overflow-hidden">
            {items.map((n) => (
              <NotificationRow
                key={n.id}
                item={n}
                onClick={() => handleClick(n)}
              />
            ))}
          </ul>

          {totalPages > 1 && (
            <Pagination
              page={page}
              totalPages={totalPages}
              onChange={setPage}
            />
          )}
        </>
      )}
    </div>
  );
}

function NotificationRow({
  item,
  onClick,
}: {
  item: Notification;
  onClick: () => void;
}) {
  const unread = !item.readAt;
  return (
    <li>
      <button
        onClick={onClick}
        className={`w-full text-left flex gap-4 px-5 py-4 transition-colors ${
          unread
            ? "bg-brand-50/40 dark:bg-brand-500/5 hover:bg-brand-50/80 dark:hover:bg-brand-500/10"
            : "hover:bg-gray-50 dark:hover:bg-white/5"
        }`}
      >
        <span
          aria-hidden="true"
          className={`mt-1.5 inline-block w-2 h-2 rounded-full shrink-0 ${TYPE_DOT_CLS[item.type] ?? "bg-gray-400"}`}
        />
        <div className="min-w-0 flex-1">
          <div className="flex items-baseline justify-between gap-3 mb-1">
            <p
              className={`text-sm text-gray-900 dark:text-white ${unread ? "font-semibold" : "font-medium"}`}
            >
              {item.title}
            </p>
            <span className="text-[11px] text-gray-400 dark:text-gray-500 tabular-nums shrink-0">
              {formatDateTime(item.createdAt)}
            </span>
          </div>
          <p className="text-xs text-gray-600 dark:text-gray-400">
            {item.body}
          </p>
          <span className="mt-2 inline-flex items-center gap-1.5 text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-500">
            <span className={`inline-block w-1.5 h-1.5 rounded-full ${TYPE_DOT_CLS[item.type]}`} />
            {TYPE_LABEL[item.type]}
            {item.resourceUrl && (
              <span className="ml-2 text-brand-600 dark:text-brand-400 normal-case tracking-normal">
                Abrir detalhes →
              </span>
            )}
          </span>
        </div>
      </button>
    </li>
  );
}

function Pagination({
  page,
  totalPages,
  onChange,
}: {
  page: number;
  totalPages: number;
  onChange: (p: number) => void;
}) {
  return (
    <div className="flex items-center justify-between">
      <button
        onClick={() => onChange(Math.max(1, page - 1))}
        disabled={page === 1}
        className="h-9 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-50 disabled:cursor-not-allowed"
      >
        ← Anterior
      </button>
      <span className="text-sm text-gray-500 dark:text-gray-400 tabular-nums">
        Página {page} de {totalPages}
      </span>
      <button
        onClick={() => onChange(Math.min(totalPages, page + 1))}
        disabled={page === totalPages}
        className="h-9 rounded-lg border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-900 px-4 text-sm font-medium text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800 disabled:opacity-50 disabled:cursor-not-allowed"
      >
        Próxima →
      </button>
    </div>
  );
}

function ListSkeleton() {
  return (
    <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] divide-y divide-gray-100 dark:divide-gray-800 overflow-hidden">
      {[0, 1, 2, 3, 4].map((i) => (
        <div key={i} className="flex gap-4 px-5 py-4 animate-pulse">
          <div className="mt-1.5 w-2 h-2 rounded-full bg-gray-200 dark:bg-gray-700 shrink-0" />
          <div className="flex-1 space-y-2">
            <div className="h-4 w-2/3 bg-gray-200 dark:bg-gray-700 rounded" />
            <div className="h-3 w-full bg-gray-100 dark:bg-gray-800 rounded" />
            <div className="h-3 w-32 bg-gray-100 dark:bg-gray-800 rounded" />
          </div>
        </div>
      ))}
    </div>
  );
}

function EmptyBox({ unreadOnly }: { unreadOnly: boolean }) {
  return (
    <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] p-12 text-center">
      <span
        className="inline-flex items-center justify-center w-12 h-12 rounded-full bg-gray-100 dark:bg-gray-800 mb-3 text-gray-400 dark:text-gray-500 mx-auto"
        aria-hidden="true"
      >
        <svg
          width="22"
          height="22"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <path d="M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
          <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
        </svg>
      </span>
      <p className="text-sm font-medium text-gray-700 dark:text-gray-300">
        {unreadOnly ? "Nada não lido" : "Você está em dia"}
      </p>
      <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 max-w-sm mx-auto">
        {unreadOnly
          ? "Todas as suas notificações já foram lidas."
          : "Quando algo importante acontecer na sua conta, você verá aqui."}
      </p>
    </div>
  );
}

function ErrorBox({ message }: { message: string }) {
  return (
    <div className="rounded-2xl border border-gray-200 bg-white dark:border-gray-800 dark:bg-white/[0.03] p-12 text-center">
      <p className="text-sm font-medium text-gray-700 dark:text-gray-300">
        Não foi possível carregar as notificações
      </p>
      <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">{message}</p>
    </div>
  );
}
