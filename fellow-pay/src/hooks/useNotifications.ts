"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { notificationsService } from "@/services/notifications.service";
import { ApiError } from "@/lib/api/client";
import type { Notification } from "@/types";

/**
 * Polling do unread count — chamado pelo bell do header. Refetch a cada 30s
 * + on window focus (default do React Query). Cheap query — só COUNT no DB.
 *
 * Operadores sem seller_id no JWT recebem 403 → query falha → count=0 silenciosamente
 * (sem polluir o header com erro). Mesmo pattern do useSellerTier.
 */
export function useUnreadNotificationsCount() {
  const query = useQuery({
    queryKey: ["notifications", "unread-count"],
    queryFn: () => notificationsService.getUnreadCount(),
    refetchInterval: 30_000, // 30s — balanceia "real-time-ish" vs custo de polling
    refetchOnWindowFocus: true,
    // 403 não é erro relevante pra UI — silencia retry pra operadores.
    retry: (failureCount, error) => {
      if (error instanceof ApiError && error.status === 403) return false;
      return failureCount < 2;
    },
  });

  // Pra 403 retornamos 0 (não polui o bell pra operadores da plataforma).
  const isOperator =
    query.error instanceof ApiError && query.error.status === 403;

  return {
    count: isOperator ? 0 : (query.data ?? 0),
    isLoading: query.isLoading,
  };
}

/**
 * Lista cheia das notificações — fetchada SÓ quando o dropdown abre (refetch
 * on mount/window focus mas sem polling agressivo). O bell mantém o count via
 * useUnreadNotificationsCount; a lista expandida custa mais e não precisa
 * polling — quando o seller abrir o dropdown, refetcha.
 */
export function useNotifications(opts?: { enabled?: boolean }) {
  const query = useQuery({
    queryKey: ["notifications", "list"],
    queryFn: () => notificationsService.list({ pageSize: 20 }),
    enabled: opts?.enabled ?? true,
    refetchOnWindowFocus: true,
    retry: (failureCount, error) => {
      if (error instanceof ApiError && error.status === 403) return false;
      return failureCount < 2;
    },
  });

  const items: Notification[] = query.data?.items ?? [];
  const isOperator =
    query.error instanceof ApiError && query.error.status === 403;

  return {
    items,
    unreadCount: query.data?.unreadCount ?? 0,
    totalCount: query.data?.totalCount ?? 0,
    isLoading: query.isLoading,
    isError: !!query.error && !isOperator,
    errorMessage:
      query.error instanceof Error ? query.error.message : null,
    isOperator,
  };
}

/**
 * Mutations: marcar uma como lida ou marcar todas. Invalidam ambas as queries
 * (list + unread-count) pra que o badge + dropdown refletem imediatamente.
 */
export function useMarkNotificationRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => notificationsService.markRead(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["notifications"] });
    },
  });
}

export function useMarkAllNotificationsRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => notificationsService.markAllRead(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["notifications"] });
    },
  });
}
