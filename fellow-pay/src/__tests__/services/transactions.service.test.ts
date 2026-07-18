import { describe, it, expect, vi, beforeEach } from "vitest";

// Mock the api client
vi.mock("@/lib/api/client", () => ({
  api: {
    get: vi.fn(),
    post: vi.fn(),
  },
}));

import { transactionsService } from "@/services/transactions.service";
import { api } from "@/lib/api/client";

describe("transactionsService", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe("list", () => {
    it("calls api.get with correct URL and no params", async () => {
      const mockResponse = { items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 };
      vi.mocked(api.get).mockResolvedValue(mockResponse);

      const result = await transactionsService.list();
      expect(api.get).toHaveBeenCalledWith("/api/v1/transactions");
      expect(result).toEqual(mockResponse);
    });

    it("includes query params when filters provided", async () => {
      const mockResponse = { items: [], totalCount: 0, page: 1, pageSize: 20, totalPages: 0 };
      vi.mocked(api.get).mockResolvedValue(mockResponse);

      await transactionsService.list({ page: 2, status: "CAPTURED", paymentType: "PIX" });
      expect(api.get).toHaveBeenCalledWith(
        "/api/v1/transactions?page=2&status=CAPTURED&paymentType=PIX"
      );
    });
  });

  describe("getById", () => {
    it("calls api.get with transaction id", async () => {
      const mockTx = { id: "abc123", amount: 10000 };
      vi.mocked(api.get).mockResolvedValue(mockTx);

      const result = await transactionsService.getById("abc123");
      expect(api.get).toHaveBeenCalledWith("/api/v1/transactions/abc123");
      expect(result).toEqual(mockTx);
    });
  });

  describe("refund", () => {
    it("calls api.post with amount and reason", async () => {
      vi.mocked(api.post).mockResolvedValue({});

      await transactionsService.refund("abc123", 5000, "Product returned");
      expect(api.post).toHaveBeenCalledWith("/api/v1/transactions/abc123/refund", {
        amount: 5000,
        reason: "Product returned",
      });
    });
  });
});
