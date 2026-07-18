import { api } from "@/lib/api/client";
import type { Notification, NotificationList, NotificationTypeCode } from "@/types";

/**
 * Backend serializa NotificationType como int (convenção do projeto). Mapeamos
 * por posição igual fizemos pra SellerTierCode. Adicionar valores ao FIM —
 * NÃO reordenar.
 */
const TYPE_BY_INDEX: NotificationTypeCode[] = [
  "TRANSACTION_CAPTURED",
  "TRANSACTION_REFUNDED",
  "DISPUTE_OPENED",
  "DISPUTE_RESOLVED",
  "PAYOUT_COMPLETED",
  "PAYOUT_FAILED",
  "TIER_UPGRADED",
  "TIER_DOWNGRADED",
  "BALANCE_RELEASED",
  "WEBHOOK_DELIVERY_FAILED",
  "SYSTEM_ANNOUNCEMENT",
];

function normalizeType(raw: unknown): NotificationTypeCode {
  if (typeof raw === "string") return raw as NotificationTypeCode;
  if (typeof raw === "number" && raw >= 0 && raw < TYPE_BY_INDEX.length) {
    return TYPE_BY_INDEX[raw];
  }
  return "SYSTEM_ANNOUNCEMENT"; // fallback defensivo
}

interface RawNotification {
  id: string;
  type: number | string;
  title: string;
  body: string;
  resourceUrl: string | null;
  metadata: Record<string, unknown> | null;
  readAt: string | null;
  createdAt: string;
}

interface RawNotificationList {
  items: RawNotification[];
  totalCount: number;
  unreadCount: number;
}

function normalize(raw: RawNotification): Notification {
  return {
    id: raw.id,
    type: normalizeType(raw.type),
    title: raw.title,
    body: raw.body,
    resourceUrl: raw.resourceUrl,
    metadata: raw.metadata,
    readAt: raw.readAt,
    createdAt: raw.createdAt,
  };
}

export const notificationsService = {
  /**
   * Lista paginada. `unreadOnly` filtra por não lidas. Returns também
   * `unreadCount` global (mesmo com unreadOnly=false) pra alimentar o badge.
   */
  async list(params?: {
    page?: number;
    pageSize?: number;
    unreadOnly?: boolean;
  }): Promise<NotificationList> {
    const search = new URLSearchParams();
    if (params?.page) search.set("page", String(params.page));
    if (params?.pageSize) search.set("pageSize", String(params.pageSize));
    if (params?.unreadOnly) search.set("unreadOnly", "true");
    const qs = search.toString();
    const url = qs
      ? `/api/v1/notifications?${qs}`
      : "/api/v1/notifications";

    const raw = await api.get<RawNotificationList>(url);
    return {
      items: raw.items.map(normalize),
      totalCount: raw.totalCount,
      unreadCount: raw.unreadCount,
    };
  },

  /**
   * Cheap query — só o count das não lidas. Usado pelo polling do badge no
   * header (refetch a cada 30s + on window focus). NÃO carrega a lista cheia.
   */
  async getUnreadCount(): Promise<number> {
    const raw = await api.get<{ count: number }>(
      "/api/v1/notifications/unread-count",
    );
    return raw.count;
  },

  async markRead(id: string): Promise<void> {
    await api.post<void>(`/api/v1/notifications/${id}/read`, {});
  },

  async markAllRead(): Promise<number> {
    const raw = await api.post<{ affected: number }>(
      "/api/v1/notifications/read-all",
      {},
    );
    return raw.affected;
  },
};
