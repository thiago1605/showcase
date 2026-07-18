import { api } from "@/lib/api/client";
import type { WebhookEndpoint, WebhookDelivery, PaginatedResponse } from "@/types";

interface CreateWebhookRequest {
  url: string;
  /** Segredo HMAC usado pra assinar payloads do webhook. Backend exige. */
  secret: string;
  events: string[];
}

export interface WebhookTestResult {
  success: boolean;
  statusCode: number;
  latencyMs: number;
  responseBody: string | null;
  error: string | null;
}

export const webhooksService = {
  async list(): Promise<WebhookEndpoint[]> {
    // Backend devolve PagedResult { items, totalCount, page, pageSize }; o front
    // só precisa do array `items` por enquanto (paginação não exposta na UI).
    const res = await api.get<PaginatedResponse<WebhookEndpoint> | WebhookEndpoint[]>(
      "/api/v1/webhook-endpoints",
    );
    if (Array.isArray(res)) return res;
    return res.items ?? [];
  },

  async getById(id: string): Promise<WebhookEndpoint> {
    return api.get<WebhookEndpoint>(`/api/v1/webhook-endpoints/${id}`);
  },

  async create(data: CreateWebhookRequest): Promise<WebhookEndpoint> {
    return api.post<WebhookEndpoint>("/api/v1/webhook-endpoints", data);
  },

  async update(id: string, data: Partial<CreateWebhookRequest>): Promise<WebhookEndpoint> {
    return api.patch<WebhookEndpoint>(`/api/v1/webhook-endpoints/${id}`, data);
  },

  async delete(id: string): Promise<void> {
    return api.delete<void>(`/api/v1/webhook-endpoints/${id}`);
  },

  async toggle(id: string, enabled: boolean): Promise<void> {
    return api.patch<void>(`/api/v1/webhook-endpoints/${id}`, { enabled });
  },

  async getDeliveries(endpointId: string, page?: number): Promise<PaginatedResponse<WebhookDelivery>> {
    const params = new URLSearchParams();
    if (page) params.set("page", String(page));
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<PaginatedResponse<WebhookDelivery>>(`/api/v1/webhook-endpoints/${endpointId}/deliveries${query}`);
  },

  async test(endpointId: string, eventType = "webhook.test"): Promise<WebhookTestResult> {
    return api.post<WebhookTestResult>(`/api/v1/webhook-endpoints/${endpointId}/test`, { eventType });
  },

  async rotateSecret(endpointId: string): Promise<{ secret: string }> {
    return api.post<{ secret: string }>(`/api/v1/webhook-endpoints/${endpointId}/rotate-secret`, {});
  },
};
