import { api } from "@/lib/api/client";
import type { PaginatedResponse } from "@/types";

export interface Dispute {
  id: string;
  transactionId: string;
  amount: number;
  reason: string;
  status: string;
  deadline: string;
  createdAt: string;
}

interface DisputeFilter {
  page?: number;
  pageSize?: number;
  status?: string;
}

export const disputesService = {
  async list(filter?: DisputeFilter): Promise<PaginatedResponse<Dispute>> {
    const params = new URLSearchParams();
    if (filter?.page) params.set("page", String(filter.page));
    if (filter?.pageSize) params.set("pageSize", String(filter.pageSize));
    if (filter?.status) params.set("status", filter.status);
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<PaginatedResponse<Dispute>>(`/api/v1/disputes${query}`);
  },

  async getById(id: string): Promise<Dispute> {
    return api.get<Dispute>(`/api/v1/disputes/${id}`);
  },
};
