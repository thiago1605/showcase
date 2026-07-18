"use client";
import { useQuery } from "@tanstack/react-query";
import { dashboardService, DashboardTimeseries } from "@/services/dashboard.service";
import { useDashboardPeriod } from "./PeriodContext";

interface TimeseriesState {
  data: DashboardTimeseries | null;
  loading: boolean;
  error: string | null;
}

/** Carrega a série temporal pro período ativo. Granularidade auto-detectada
 *  no backend (Day ≤60d, Week >60d). Cache via React Query. */
export function useDashboardTimeseries(): TimeseriesState {
  const { period } = useDashboardPeriod();

  // Pra preset "Hoje" forçamos granularidade horária — caso contrário o gráfico
  // vira um único ponto (24h cabem num bucket diário). Demais presets deixam o
  // backend escolher (Day ou Week).
  const granularity = period.preset === "TODAY" ? "Hour" : undefined;

  const query = useQuery({
    queryKey: ["dashboard", "timeseries", period.from, period.to, period.preset, granularity],
    queryFn: () => dashboardService.getTimeseries({ from: period.from, to: period.to, granularity }),
  });

  return {
    data: query.data ?? null,
    loading: query.isLoading,
    error: query.error instanceof Error ? query.error.message : query.error ? "Erro ao carregar série temporal." : null,
  };
}

