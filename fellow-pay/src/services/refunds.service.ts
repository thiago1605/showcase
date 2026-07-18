import { api } from "@/lib/api/client";
import type { PaginatedResponse } from "@/types";

export interface RefundIntent {
  id: string;
  tenantId: string;
  transactionId: string;
  sellerId: string | null;
  amount: number;
  reason: string | null;
  status: string;
  providerRefundId: string | null;
  attemptCount: number;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
}

interface RefundFilter {
  page?: number;
  pageSize?: number;
  status?: string;
  from?: string;
  to?: string;
}

export const refundsService = {
  async list(filter?: RefundFilter): Promise<PaginatedResponse<RefundIntent>> {
    const params = new URLSearchParams();
    if (filter?.page) params.set("page", String(filter.page));
    if (filter?.pageSize) params.set("pageSize", String(filter.pageSize));
    if (filter?.status) params.set("status", filter.status);
    if (filter?.from) params.set("from", filter.from);
    if (filter?.to) params.set("to", filter.to);
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<PaginatedResponse<RefundIntent>>(`/api/v1/refunds${query}`);
  },
};
