import { api } from "@/lib/api/client";
import type { Subscription, PaginatedResponse } from "@/types";

interface SubscriptionFilter {
  page?: number;
  pageSize?: number;
  status?: string;
}

interface CreateSubscriptionRequest {
  customerId: string;
  amount: number;
  description: string;
  interval: string;
  maxCycles?: number;
}

export const subscriptionsService = {
  async list(filter?: SubscriptionFilter): Promise<PaginatedResponse<Subscription>> {
    const params = new URLSearchParams();
    if (filter?.page) params.set("page", String(filter.page));
    if (filter?.pageSize) params.set("pageSize", String(filter.pageSize));
    if (filter?.status) params.set("status", filter.status);
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<PaginatedResponse<Subscription>>(`/api/v1/subscriptions${query}`);
  },

  async getById(id: string): Promise<Subscription> {
    return api.get<Subscription>(`/api/v1/subscriptions/${id}`);
  },

  async create(data: CreateSubscriptionRequest): Promise<Subscription> {
    return api.post<Subscription>("/api/v1/subscriptions", data);
  },

  async cancel(id: string): Promise<void> {
    return api.patch<void>(`/api/v1/subscriptions/${id}/cancel`, {});
  },

  async pause(id: string): Promise<void> {
    return api.patch<void>(`/api/v1/subscriptions/${id}/pause`, {});
  },

  async resume(id: string): Promise<void> {
    return api.patch<void>(`/api/v1/subscriptions/${id}/resume`, {});
  },
};
