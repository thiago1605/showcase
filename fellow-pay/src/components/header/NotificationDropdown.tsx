"use client";
import React, { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Dropdown } from "../ui/dropdown/Dropdown";
import { LiquidGlassSurface } from "@/components/ui/LiquidGlassSurface";
import { useTheme } from "@/context/ThemeContext";
import {
  useNotifications,
  useUnreadNotificationsCount,
  useMarkNotificationRead,
  useMarkAllNotificationsRead,
} from "@/hooks/useNotifications";
import { useNotificationsRealtime } from "@/hooks/useNotificationsRealtime";
import type { Notification, NotificationTypeCode } from "@/types";

/**
 * Dropdown de notificações in-app — Sprint 2 Fase 1 (polling-based).
 *
 * Arquitetura:
 *  - **Badge count**: useUnreadNotificationsCount faz polling a cada 30s
 *    (cheap COUNT query no backend). Refletido na bolinha laranja do bell.
 *  - **Lista**: useNotifications fetcha quando o dropdown abre (enabled
 *    apenas quando isOpen). Evita carregar 20 items toda hora.
 *  - **Read tracking**: click em uma notificação chama markRead + navega
 *    pra resourceUrl se houver. "Marcar todas como lidas" bulk endpoint.
 *
 * Operadores da plataforma (sem seller_id no JWT) recebem 403 do backend —
 * hooks tratam silenciosamente (count=0, lista vazia) sem mostrar erro.
 *
 * Fase 2 (futura): SignalR push pra invalidar a query em real-time quando
 * o backend cria uma notificação — polling fica como fallback.
 */
export default function NotificationDropdown() {
  const [isOpen, setIsOpen] = useState(false);
  const router = useRouter();
  // Tint do liquid glass condicional pelo tema — branco translúcido no
  // light mode, gray-900 translúcido no dark. Sem isso o dark mode fica
  // com tint branco sobre fundo escuro, ficando "luminoso".
  const { theme } = useTheme();
  const glassTint =
    theme === "dark" ? "rgba(46,26,79,0.85)" : "rgba(255,255,255,0.92)";

  // SignalR client — conecta ao hub `/hubs/notifications` e invalida queries
  // ao receber `notification.created`. Polling de 30s segue como fallback.
  useNotificationsRealtime();

  // Polling background — não depende do isOpen, sempre roda pro badge.
  const { count: unreadCount } = useUnreadNotificationsCount();
  // Lista fetchada só quando o dropdown está aberto (lazy + refetch on focus).
  const { items, isLoading, isError } = useNotifications({ enabled: isOpen });

  const markRead = useMarkNotificationRead();
  const markAllRead = useMarkAllNotificationsRead();

  function toggleDropdown() {
    setIsOpen((v) => !v);
  }

  function closeDropdown() {
    setIsOpen(false);
  }

  function handleClickNotification(n: Notification) {
    // Marca como lida (idempotente — backend ignora se já lida).
    if (!n.readAt) {
      markRead.mutate(n.id);
    }
    closeDropdown();
    if (n.resourceUrl) {
      router.push(n.resourceUrl);
    }
  }

  function handleMarkAllRead(e: React.MouseEvent) {
    e.stopPropagation();
    if (unreadCount === 0) return;
    markAllRead.mutate();
  }

  return (
    <div className="relative">
      <button
        className="relative dropdown-toggle flex items-center justify-center text-gray-500 transition-colors bg-white border border-gray-200 rounded-full hover:text-gray-700 h-10 w-10 hover:bg-gray-100 dark:border-gray-800 dark:bg-gray-900 dark:text-gray-400 dark:hover:bg-gray-800 dark:hover:text-white"
        onClick={toggleDropdown}
        aria-label={
          unreadCount > 0
            ? `Notificações (${unreadCount} não ${unreadCount === 1 ? "lida" : "lidas"})`
            : "Notificações"
        }
      >
        {unreadCount > 0 && (
          <span
            className="absolute right-0 top-0.5 z-10 h-2 w-2 rounded-full bg-orange-400"
            aria-hidden="true"
          >
            <span className="absolute inline-flex w-full h-full bg-orange-400 rounded-full opacity-75 animate-ping"></span>
          </span>
        )}
        <svg
          className="fill-current"
          width="20"
          height="20"
          viewBox="0 0 20 20"
          xmlns="http://www.w3.org/2000/svg"
          aria-hidden="true"
        >
          <path
            fillRule="evenodd"
            clipRule="evenodd"
            d="M10.75 2.29248C10.75 1.87827 10.4143 1.54248 10 1.54248C9.58583 1.54248 9.25004 1.87827 9.25004 2.29248V2.83613C6.08266 3.20733 3.62504 5.9004 3.62504 9.16748V14.4591H3.33337C2.91916 14.4591 2.58337 14.7949 2.58337 15.2091C2.58337 15.6234 2.91916 15.9591 3.33337 15.9591H4.37504H15.625H16.6667C17.0809 15.9591 17.4167 15.6234 17.4167 15.2091C17.4167 14.7949 17.0809 14.4591 16.6667 14.4591H16.375V9.16748C16.375 5.9004 13.9174 3.20733 10.75 2.83613V2.29248ZM14.875 14.4591V9.16748C14.875 6.47509 12.6924 4.29248 10 4.29248C7.30765 4.29248 5.12504 6.47509 5.12504 9.16748V14.4591H14.875ZM8.00004 17.7085C8.00004 18.1228 8.33583 18.4585 8.75004 18.4585H11.25C11.6643 18.4585 12 18.1228 12 17.7085C12 17.2943 11.6643 16.9585 11.25 16.9585H8.75004C8.33583 16.9585 8.00004 17.2943 8.00004 17.7085Z"
            fill="currentColor"
          />
        </svg>
      </button>

      <Dropdown
        isOpen={isOpen}
        onClose={closeDropdown}
        className="-right-[240px] mt-1 flex w-[350px] flex-col rounded-2xl overflow-hidden border border-gray-200 shadow-theme-lg dark:border-gray-800 sm:w-[361px] lg:right-0"
      >
        {/* Header purple gradient — mesma estética do header do UserDropdown.
            Substitui o bg-white/gray-dark que ficava apagado em dark mode.
            Texto branco pra contraste sobre o gradient. */}
        <div className="relative overflow-hidden flex items-center justify-between bg-gradient-to-br from-brand-500 to-brand-700 px-3 pt-3 pb-3 text-white">
          <span
            aria-hidden="true"
            className="pointer-events-none absolute inset-x-0 top-0 h-1/2 bg-gradient-to-b from-white/15 to-transparent"
          />
          <h5 className="relative z-10 text-base font-semibold text-white">
            Notificações
          </h5>
          <div className="relative z-10 flex items-center gap-2">
            {unreadCount > 0 && (
              <button
                onClick={handleMarkAllRead}
                disabled={markAllRead.isPending}
                className="text-[11px] font-medium text-white/95 hover:underline disabled:opacity-50"
              >
                Marcar todas como lidas
              </button>
            )}
            <button
              onClick={closeDropdown}
              aria-label="Fechar"
              className="text-white/80 transition hover:text-white"
            >
              <svg
                className="fill-current"
                width="20"
                height="20"
                viewBox="0 0 24 24"
                xmlns="http://www.w3.org/2000/svg"
                aria-hidden="true"
              >
                <path
                  fillRule="evenodd"
                  clipRule="evenodd"
                  d="M6.21967 7.28131C5.92678 6.98841 5.92678 6.51354 6.21967 6.22065C6.51256 5.92775 6.98744 5.92775 7.28033 6.22065L11.999 10.9393L16.7176 6.22078C17.0105 5.92789 17.4854 5.92788 17.7782 6.22078C18.0711 6.51367 18.0711 6.98855 17.7782 7.28144L13.0597 12L17.7782 16.7186C18.0711 17.0115 18.0711 17.4863 17.7782 17.7792C17.4854 18.0721 17.0105 18.0721 16.7176 17.7792L11.999 13.0607L7.28033 17.7794C6.98744 18.0722 6.51256 18.0722 6.21967 17.7794C5.92678 17.4865 5.92678 17.0116 6.21967 16.7187L10.9384 12L6.21967 7.28131Z"
                  fill="currentColor"
                />
              </svg>
            </button>
          </div>
        </div>

        {/* Body com liquid glass effect (mesma config do UserDropdown body) —
            header acima fica sólido, body abaixo recebe o tint translúcido
            + blur+distortion. Tint 0.92 dá legibilidade aos itens com um
            traço sutil de glass. */}
        <LiquidGlassSurface
          rounded="rounded-none"
          bounce={false}
          subtle
          tint={glassTint}
          className="flex flex-col p-3"
        >
        {isLoading ? (
          <LoadingState />
        ) : isError ? (
          <ErrorState />
        ) : items.length === 0 ? (
          <EmptyState />
        ) : (
          <ul className="flex flex-col h-auto max-h-[400px] overflow-y-auto custom-scrollbar -mx-1">
            {items.map((n) => (
              <NotificationRow
                key={n.id}
                item={n}
                onClick={() => handleClickNotification(n)}
              />
            ))}
          </ul>
        )}

        {/* Footer com link pra página cheia — sempre visível pra dar acesso
            ao histórico mesmo quando o dropdown está vazio. */}
        <Link
          href="/notifications"
          onClick={closeDropdown}
          className="mt-3 block w-full rounded-lg bg-brand-500 hover:bg-brand-600 active:scale-[0.998] px-3 py-2 text-center text-[11px] font-medium text-white transition-colors"
        >
          Ver todas as notificações
        </Link>
        </LiquidGlassSurface>
      </Dropdown>
    </div>
  );
}

function LoadingState() {
  return (
    <div className="flex flex-col gap-2 px-1 py-2">
      {[0, 1, 2].map((i) => (
        <div
          key={i}
          className="h-14 w-full animate-pulse bg-gray-100 dark:bg-gray-800 rounded-lg"
        />
      ))}
    </div>
  );
}

function ErrorState() {
  return (
    <div className="flex flex-col items-center justify-center py-8 px-4 text-center">
      <span
        className="inline-flex items-center justify-center w-10 h-10 rounded-full bg-gray-100 dark:bg-gray-800 mb-2 text-gray-400 dark:text-gray-500"
        aria-hidden="true"
      >
        <svg
          width="20"
          height="20"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
          strokeLinejoin="round"
        >
          <circle cx="12" cy="12" r="10" />
          <line x1="12" y1="8" x2="12" y2="12" />
          <line x1="12" y1="16" x2="12.01" y2="16" />
        </svg>
      </span>
      <p className="text-sm font-medium text-gray-700 dark:text-gray-300">
        Não foi possível carregar as notificações
      </p>
      <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
        Tente novamente em instantes.
      </p>
    </div>
  );
}

function EmptyState() {
  return (
    <div className="flex flex-col items-center justify-center py-10 px-4 text-center">
      <span
        className="inline-flex items-center justify-center w-12 h-12 rounded-full bg-gray-100 dark:bg-gray-800 mb-3 text-gray-400 dark:text-gray-500"
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
        Você está em dia
      </p>
      <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 max-w-[260px]">
        Avisamos por aqui quando algo importante acontecer — captura, reembolso,
        saque ou mudança de nível.
      </p>
    </div>
  );
}

/**
 * Cor do bullet do tipo de notificação. Mapeado pra semântica:
 *  - Verde: ações positivas (captura, payout, tier upgrade)
 *  - Vermelho: ações negativas (dispute, payout failed, downgrade)
 *  - Roxo: marcos importantes (tier, anúncios)
 *  - Cinza: neutros (refund — não é positivo nem negativo, é só info)
 */
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
        className={`w-full text-left flex gap-3 rounded-lg px-3 py-3 transition-colors ${
          unread
            ? "bg-brand-50/40 dark:bg-brand-500/5 hover:bg-brand-50/80 dark:hover:bg-brand-500/10"
            : "hover:bg-gray-50 dark:hover:bg-white/5"
        }`}
      >
        <span
          aria-hidden="true"
          className={`mt-1.5 inline-block w-2 h-2 rounded-full shrink-0 ${TYPE_DOT_CLS[item.type] ?? "bg-gray-400"}`}
        />
        <span className="block min-w-0 flex-1">
          <span
            className={`block text-theme-sm text-gray-800 dark:text-gray-200 ${
              unread ? "font-semibold" : "font-medium"
            }`}
          >
            {item.title}
          </span>
          <span className="block text-theme-xs text-gray-500 dark:text-gray-400 mt-0.5 line-clamp-2">
            {item.body}
          </span>
          <span className="block text-[10px] text-gray-400 dark:text-gray-500 mt-1 tabular-nums">
            {formatRelativeTime(item.createdAt)}
          </span>
        </span>
      </button>
    </li>
  );
}

/**
 * Formato relativo PT-BR compacto (agora / 5 min / 2h / 3 dias). Sem libs —
 * implementação mínima que cobre os ranges relevantes pra dropdown de
 * notificações. Notificações antigas (>30 dias) caem em "30+ dias".
 */
function formatRelativeTime(iso: string): string {
  const now = Date.now();
  const then = new Date(iso).getTime();
  const diffMs = now - then;
  const minutes = Math.floor(diffMs / 60_000);
  if (minutes < 1) return "agora";
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days} dia${days === 1 ? "" : "s"}`;
  return "30+ dias";
}
