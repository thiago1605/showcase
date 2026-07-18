import { api } from "@/lib/api/client";
import type { SellerBalance } from "@/types";

// Dashboard data is always scoped to the authenticated seller.
// The sellerId is inferred from the JWT token on the backend.

export interface SellerDashboardSummary {
  totalVolume: number;
  totalFees: number;
  totalNet: number;
  totalPlatformFees: number;
  totalProviderCosts: number;
  totalPlatformMargin: number;
  marginPercent: number;
  transactionCount: number;
  // Métricas restritas a transações capturadas — KPIs principais do dashboard
  // devem ignorar tentativas recusadas/pendentes pra não distorcer a leitura.
  capturedVolume: number;
  capturedFees: number;
  capturedNet: number;
  capturedCount: number;
  byStatus: { status: string | number; count: number; volume: number }[];
  byPaymentType: { paymentType: string | number; count: number; volume: number }[];
  /** Cross-tab status × método — usado pelo PendingFundsCard pra detalhar
   *  quais métodos contribuem pra pendência ou recusa. */
  byStatusAndMethod: { status: string | number; paymentType: string | number; count: number; volume: number }[];
  byProvider: {
    provider: string | number;
    count: number;
    volume: number;
    platformFees: number;
    providerCosts: number;
    margin: number;
  }[];
}

export interface DashboardFilter {
  from?: string;
  to?: string;
}

// Backend serializa enum como int por padrão (System.Text.Json) — granularity
// chega como 0 (Day) ou 1 (Week). Mantemos assim e normalizamos no widget.
export interface DashboardTimeseriesPoint {
  date: string;
  volume: number;
  net: number;
  fees: number;
  margin: number;
  count: number;
}

export interface DashboardTimeseries {
  granularity: number | string;
  from: string;
  to: string;
  points: DashboardTimeseriesPoint[];
}

export interface TopCustomer {
  email: string;
  name: string | null;
  count: number;
  volume: number;
}

export interface TopPaymentLink {
  paymentLinkId: string;
  name: string;
  token: string;
  count: number;
  volume: number;
}

/**
 * Produto mais vendido no período. Backend resolve via
 * `Transaction.ExternalReferenceId == "product:{guid}"` — só capturadas
 * entram. `name` cai pra "(produto removido)" quando o produto foi
 * deletado mas as transações continuam históricas.
 */
export interface TopProduct {
  productId: string;
  name: string;
  slug: string | null;
  coverImageUrl: string | null;
  count: number;
  volume: number;
}

export interface HeatmapCell {
  dayOfWeek: number; // 0=Dom .. 6=Sáb
  hour: number;      // 0..23
  count: number;
  volume: number;
}

export interface DashboardHeatmap {
  from: string;
  to: string;
  cells: HeatmapCell[];
}

export interface ConversionByMethod {
  paymentType: number | string;
  total: number;
  captured: number;
  pending: number;
  declined: number;
  capturedVolume: number;
  approvalRate: number;
}

export interface TicketDistributionBin {
  label: string;
  minAmount: number;
  maxAmount: number | null;
  count: number;
  volume: number;
}

export interface TicketDistribution {
  totalCount: number;
  averageTicket: number;
  medianTicket: number;
  bins: TicketDistributionBin[];
}

export interface CustomerRetention {
  uniqueCustomers: number;
  returningCustomers: number;
  newCustomers: number;
  repeatInPeriod: number;
  returningRate: number;
  repeatInPeriodRate: number;
}

export const dashboardService = {
  async getSummary(filter?: DashboardFilter): Promise<SellerDashboardSummary> {
    const params = new URLSearchParams();
    if (filter?.from) params.set("from", filter.from);
    if (filter?.to) params.set("to", filter.to);
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<SellerDashboardSummary>(`/api/v1/dashboard${query}`);
  },

  async getBalance(): Promise<SellerBalance> {
    // Uses seller ID from JWT - no explicit sellerId param
    return api.get<SellerBalance>("/api/v1/sellers/me/balance");
  },

  async getTimeseries(filter?: DashboardFilter & { granularity?: "Day" | "Week" | "Hour" }): Promise<DashboardTimeseries> {
    const params = new URLSearchParams();
    if (filter?.from) params.set("from", filter.from);
    if (filter?.to) params.set("to", filter.to);
    if (filter?.granularity) params.set("granularity", filter.granularity);
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<DashboardTimeseries>(`/api/v1/dashboard/timeseries${query}`);
  },

  async getTopCustomers(filter?: DashboardFilter, limit = 5): Promise<TopCustomer[]> {
    const params = new URLSearchParams({ limit: String(limit) });
    if (filter?.from) params.set("from", filter.from);
    if (filter?.to) params.set("to", filter.to);
    return api.get<TopCustomer[]>(`/api/v1/dashboard/top-customers?${params.toString()}`);
  },

  async getTopPaymentLinks(filter?: DashboardFilter, limit = 5): Promise<TopPaymentLink[]> {
    const params = new URLSearchParams({ limit: String(limit) });
    if (filter?.from) params.set("from", filter.from);
    if (filter?.to) params.set("to", filter.to);
    return api.get<TopPaymentLink[]>(`/api/v1/dashboard/top-payment-links?${params.toString()}`);
  },

  async getTopProducts(filter?: DashboardFilter, limit = 5): Promise<TopProduct[]> {
    const params = new URLSearchParams({ limit: String(limit) });
    if (filter?.from) params.set("from", filter.from);
    if (filter?.to) params.set("to", filter.to);
    return api.get<TopProduct[]>(`/api/v1/dashboard/top-products?${params.toString()}`);
  },

  async getHeatmap(filter?: DashboardFilter): Promise<DashboardHeatmap> {
    const params = new URLSearchParams();
    if (filter?.from) params.set("from", filter.from);
    if (filter?.to) params.set("to", filter.to);
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<DashboardHeatmap>(`/api/v1/dashboard/heatmap${query}`);
  },

  async getConversionByMethod(filter?: DashboardFilter): Promise<ConversionByMethod[]> {
    const params = new URLSearchParams();
    if (filter?.from) params.set("from", filter.from);
    if (filter?.to) params.set("to", filter.to);
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<ConversionByMethod[]>(`/api/v1/dashboard/conversion${query}`);
  },

  async getTicketDistribution(filter?: DashboardFilter): Promise<TicketDistribution> {
    const params = new URLSearchParams();
    if (filter?.from) params.set("from", filter.from);
    if (filter?.to) params.set("to", filter.to);
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<TicketDistribution>(`/api/v1/dashboard/ticket-distribution${query}`);
  },

  async getCustomerRetention(filter?: DashboardFilter): Promise<CustomerRetention> {
    const params = new URLSearchParams();
    if (filter?.from) params.set("from", filter.from);
    if (filter?.to) params.set("to", filter.to);
    const query = params.toString() ? `?${params.toString()}` : "";
    return api.get<CustomerRetention>(`/api/v1/dashboard/customer-retention${query}`);
  },
};
