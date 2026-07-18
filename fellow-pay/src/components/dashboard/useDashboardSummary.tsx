"use client";
import { useQuery } from "@tanstack/react-query";
import { dashboardService, SellerDashboardSummary } from "@/services/dashboard.service";
import { useDashboardPeriod } from "./PeriodContext";

interface SummaryState {
  current: SellerDashboardSummary | null;
  previous: SellerDashboardSummary | null;
  loading: boolean;
  error: string | null;
}

/**
 * Carrega summary do período atual + período anterior pra deltas. Os dois
 * compartilham cache via React Query — se outro lugar pedir o mesmo recorte,
 * dedup automático.
 */
export function useDashboardSummary(): SummaryState {
  const { period } = useDashboardPeriod();

  const current = useQuery({
    queryKey: ["dashboard", "summary", period.from, period.to],
    queryFn: () => dashboardService.getSummary({ from: period.from, to: period.to }),
  });

  const previous = useQuery({
    queryKey: ["dashboard", "summary", period.prevFrom, period.prevTo],
    queryFn: () => dashboardService.getSummary({ from: period.prevFrom, to: period.prevTo }),
  });

  return {
    current: current.data ?? null,
    previous: previous.data ?? null,
    loading: current.isLoading,
    error: current.error instanceof Error ? current.error.message : current.error ? "Erro ao carregar dashboard." : null,
  };
}
