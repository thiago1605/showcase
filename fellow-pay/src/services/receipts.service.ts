import { api } from "@/lib/api/client";
import { getCurrentSellerId } from "@/context/AuthContext";

export interface Receipt {
  id: string;
  sellerId: string;
  transactionId: string | null;
  payoutId: string | null;
  type: string;
  status: string;
  amount: number;
  description: string | null;
  createdAt: string;
}

export interface ReceiptDetail extends Receipt {
  tenantId?: string;
  refundIntentId?: string | null;
  provider?: string;
  providerReceiptId?: string | null;
  pdfStorageKey?: string | null;
  publicUrl?: string | null;
  currency?: string;
}

export const receiptsService = {
  /**
   * Lists the authenticated seller's receipts.
   * Backend route: `GET /api/v1/receipts/seller/{sellerId}` returns a non-paginated array.
   * The seller id is read from the cached AuthUser (claim `seller_id` in the JWT).
   */
  async listMine(): Promise<Receipt[]> {
    const sellerId = getCurrentSellerId();
    if (!sellerId) return [];
    return api.get<Receipt[]>(`/api/v1/receipts/seller/${sellerId}`);
  },

  async getById(id: string): Promise<ReceiptDetail> {
    return api.get<ReceiptDetail>(`/api/v1/receipts/${id}`);
  },
};
