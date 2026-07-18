/**
 * Producer-scoped webhooks service.
 *
 * Diferença vs `webhooks.service.ts` (que é dev/admin tenant-wide):
 *   - Esta API é seller-scoped. O backend deriva o seller_id do JWT e:
 *     - GET /api/v1/webhook-endpoints → lista APENAS os endpoints do seller
 *       (filtra automaticamente por SellerId == JWT.seller_id).
 *     - POST → cria endpoint com SellerId setado.
 *   - Não precisa passar sellerId nem nada — o backend faz o scoping.
 *
 * Usado pela página /integrations (CONTA do sidebar) — onde o producer
 * configura integração com ferramentas de marketing automation (RD Station,
 * ActiveCampaign, Mailchimp) recebendo webhook por venda.
 */
import { api } from "@/lib/api/client";
import type { WebhookEndpoint, WebhookDelivery, PaginatedResponse } from "@/types";

interface CreateProducerWebhookRequest {
  url: string;
  /** Segredo HMAC usado pra assinar payloads. Backend exige mínimo 16 chars. */
  secret: string;
  events: string[];
}

export interface ProducerWebhookTestResult {
  success: boolean;
  statusCode: number;
  latencyMs: number;
  responseBody: string | null;
  error: string | null;
}

/**
 * Eventos disponíveis pra producer webhooks. Foco em marketing automation:
 * - transaction.captured: venda confirmada (lead "comprou")
 * - transaction.refunded: estorno (precisa remover do funil)
 * - subscription.created: novo assinante recorrente
 * - subscription.canceled: churn
 */
export const PRODUCER_WEBHOOK_EVENTS = [
  "transaction.captured",
  "transaction.refunded",
  "subscription.created",
  "subscription.canceled",
  "subscription.renewed",
] as const;

export const producerWebhooksService = {
  async listMyWebhooks(): Promise<WebhookEndpoint[]> {
    // Backend filtra automaticamente pelo seller_id do JWT.
    const res = await api.get<PaginatedResponse<WebhookEndpoint> | WebhookEndpoint[]>(
      "/api/v1/webhook-endpoints",
    );
    if (Array.isArray(res)) return res;
    return res.items ?? [];
  },

  async createMyWebhook(data: CreateProducerWebhookRequest): Promise<WebhookEndpoint> {
    return api.post<WebhookEndpoint>("/api/v1/webhook-endpoints", data);
  },

  async deleteMyWebhook(id: string): Promise<void> {
    return api.delete<void>(`/api/v1/webhook-endpoints/${id}`);
  },

  async toggleMyWebhook(id: string, enabled: boolean): Promise<void> {
    return api.patch<void>(`/api/v1/webhook-endpoints/${id}`, { enabled });
  },

  async getDeliveries(endpointId: string, page?: number): Promise<PaginatedResponse<WebhookDelivery>> {
    const params = new URLSearchParams();
    if (page) params.set("page", String(page));
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<PaginatedResponse<WebhookDelivery>>(
      `/api/v1/webhook-endpoints/${endpointId}/deliveries${query}`,
    );
  },

  async testMyWebhook(
    endpointId: string,
    eventType = "webhook.test",
  ): Promise<ProducerWebhookTestResult> {
    return api.post<ProducerWebhookTestResult>(
      `/api/v1/webhook-endpoints/${endpointId}/test`,
      { eventType },
    );
  },

  async rotateSecret(endpointId: string): Promise<{ secret: string }> {
    return api.post<{ secret: string }>(
      `/api/v1/webhook-endpoints/${endpointId}/rotate-secret`,
      {},
    );
  },
};
