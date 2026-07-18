"use client";
import React, { useMemo } from "react";
import dynamic from "next/dynamic";
import type { ApexOptions } from "apexcharts";
import { useQuery } from "@tanstack/react-query";
import { dashboardService } from "@/services/dashboard.service";
import { useDashboardPeriod } from "./PeriodContext";
import { useChartTheme } from "./useChartTheme";
import { EmptyStateCTA } from "./EmptyStateCTA";

const ReactApexChart = dynamic(() => import("react-apexcharts"), { ssr: false });

export function CustomerRetentionWidget() {
  const { period } = useDashboardPeriod();
  const chartTheme = useChartTheme();

  const { data, isLoading, error } = useQuery({
    queryKey: ["dashboard", "customer-retention", period.from, period.to],
    queryFn: () => dashboardService.getCustomerRetention({ from: period.from, to: period.to }),
  });

  const { series, labels } = useMemo(() => {
    if (!data) return { series: [], labels: [] };
    return {
      series: [data.newCustomers, data.returningCustomers],
      labels: ["Novos", "Recorrentes"],
    };
  }, [data]);

  const options: ApexOptions = useMemo(
    () => ({
      chart: { fontFamily: "Outfit, sans-serif", type: "donut", toolbar: { show: false }, background: "transparent" },
      theme: { mode: chartTheme.isDark ? "dark" : "light" },
      labels,
      colors: ["#3B82F6", "#22C55E"],
      legend: {
        position: "bottom",
        fontSize: "12px",
        labels: { colors: chartTheme.muted },
        markers: { size: 6 },
      },
      dataLabels: { enabled: false },
      stroke: { width: 0 },
      plotOptions: {
        pie: {
          donut: {
            size: "65%",
            labels: {
              show: true,
              name: { show: true, fontSize: "13px", color: chartTheme.muted },
              value: {
                show: true,
                fontSize: "20px",
                fontWeight: 700,
                color: chartTheme.text,
                formatter: (val: string) => val,
              },
              total: {
                show: true,
                showAlways: true,
                label: "Clientes",
                fontSize: "13px",
                color: chartTheme.muted,
                formatter: () => String(data?.uniqueCustomers ?? 0),
              },
            },
          },
        },
      },
      tooltip: {
        theme: chartTheme.isDark ? "dark" : "light",
        y: { formatter: (val: number) => `${val} cliente${val === 1 ? "" : "s"}` },
      },
    }),
    [labels, chartTheme, data]
  );

  return (
    <div className="rounded-3xl border border-gray-200/60 bg-white dark:border-gray-800 dark:bg-gray-900 h-full">
      <div className="px-5 py-4 border-b border-gray-200 dark:border-gray-800">
        <h3 className="text-sm font-semibold text-gray-900 dark:text-white">Retenção de clientes</h3>
        <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
          Identificados por email; histórico até 1 ano antes do período
        </p>
      </div>
      <div className="p-5">
        {isLoading && <div className="h-64 bg-gray-100 dark:bg-gray-800 rounded animate-pulse" />}

        {error && !isLoading && (
          <p className="text-sm text-error-600 dark:text-error-400">
            {error instanceof Error ? error.message : "Erro ao carregar."}
          </p>
        )}

        {!isLoading && !error && data && data.uniqueCustomers === 0 && (
          <EmptyStateCTA
            title="Sem clientes identificados"
            description="Quando seus clientes informarem email no checkout, esta seção mostra novos vs recorrentes."
          />
        )}

        {!isLoading && !error && data && data.uniqueCustomers > 0 && (
          <>
            <div role="img" aria-label={`Composição de ${data.uniqueCustomers} clientes únicos: ${data.newCustomers} novos e ${data.returningCustomers} recorrentes.`}>
              <ReactApexChart options={options} series={series} type="donut" height={220} />
            </div>

            <div className="grid grid-cols-2 gap-3 pt-4 border-t border-gray-100 dark:border-gray-800">
              <div className="rounded-lg bg-gray-50 dark:bg-gray-800/50 p-3 text-center">
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Taxa de retorno</p>
                <p className="text-lg font-semibold text-success-600 dark:text-success-400">
                  {data.returningRate.toFixed(1)}%
                </p>
                <p className="text-[10px] text-gray-500 dark:text-gray-400 mt-0.5">
                  {data.returningCustomers} de {data.uniqueCustomers} já compraram antes
                </p>
              </div>
              <div className="rounded-lg bg-gray-50 dark:bg-gray-800/50 p-3 text-center">
                <p className="text-[10px] uppercase tracking-wider text-gray-500 dark:text-gray-400">Repetiram no período</p>
                <p className="text-lg font-semibold text-brand-600 dark:text-brand-400">
                  {data.repeatInPeriodRate.toFixed(1)}%
                </p>
                <p className="text-[10px] text-gray-500 dark:text-gray-400 mt-0.5">
                  {data.repeatInPeriod} cliente{data.repeatInPeriod === 1 ? "" : "s"} com {">"} 1 compra
                </p>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
