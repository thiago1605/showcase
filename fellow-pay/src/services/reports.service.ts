import { api } from "@/lib/api/client";

export interface ScheduledReport {
  id: string;
  name: string;
  type: string;
  format: string;
  frequency: string;
  lastSentAt: string;
  enabled: boolean;
  createdAt: string;
}

interface CreateReportRequest {
  name: string;
  type: string;
  format: string;
  frequency: string;
}

export const reportsService = {
  async list(): Promise<ScheduledReport[]> {
    return api.get<ScheduledReport[]>("/api/v1/scheduled-reports");
  },

  async create(data: CreateReportRequest): Promise<ScheduledReport> {
    return api.post<ScheduledReport>("/api/v1/scheduled-reports", data);
  },

  async delete(id: string): Promise<void> {
    return api.delete<void>(`/api/v1/scheduled-reports/${id}`);
  },

  async toggle(id: string, enabled: boolean): Promise<void> {
    return api.patch<void>(`/api/v1/scheduled-reports/${id}`, { enabled });
  },
};
