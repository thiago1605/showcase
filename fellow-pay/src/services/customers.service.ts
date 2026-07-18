import { api } from "@/lib/api/client";
import type { Customer, PaginatedResponse } from "@/types";

interface CustomerFilter {
  page?: number;
  pageSize?: number;
  search?: string;
}

export const customersService = {
  async list(filter?: CustomerFilter): Promise<PaginatedResponse<Customer>> {
    const params = new URLSearchParams();
    if (filter?.page) params.set("page", String(filter.page));
    if (filter?.pageSize) params.set("pageSize", String(filter.pageSize));
    if (filter?.search) params.set("search", filter.search);
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<PaginatedResponse<Customer>>(`/api/v1/customers${query}`);
  },

  async getById(id: string): Promise<Customer> {
    return api.get<Customer>(`/api/v1/customers/${id}`);
  },
};
