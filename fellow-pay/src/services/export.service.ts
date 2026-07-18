import { api } from "@/lib/api/client";

export const exportService = {
  async exportTransactions(format: "csv" | "pdf", filters?: { status?: string; from?: string; to?: string }): Promise<Blob> {
    const params = new URLSearchParams();
    params.set("format", format);
    if (filters?.status) params.set("status", filters.status);
    if (filters?.from) params.set("from", filters.from);
    if (filters?.to) params.set("to", filters.to);
    const token = typeof window !== "undefined" ? sessionStorage.getItem("fellow_access_token") : null;
    const baseUrl = process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";
    const response = await fetch(`${baseUrl}/api/v1/transactions/export?${params.toString()}`, {
      headers: token ? { Authorization: `Bearer ${token}` } : {},
    });
    if (!response.ok) throw new Error("Export failed");
    return response.blob();
  },

  async exportPayouts(format: "csv" | "pdf"): Promise<Blob> {
    const token = typeof window !== "undefined" ? sessionStorage.getItem("fellow_access_token") : null;
    const baseUrl = process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:5000";
    const response = await fetch(`${baseUrl}/api/v1/payouts/export?format=${format}`, {
      headers: token ? { Authorization: `Bearer ${token}` } : {},
    });
    if (!response.ok) throw new Error("Export failed");
    return response.blob();
  },

  downloadBlob(blob: Blob, filename: string) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  },
};
