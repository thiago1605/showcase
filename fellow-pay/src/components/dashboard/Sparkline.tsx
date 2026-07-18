"use client";
import React, { useMemo } from "react";
import dynamic from "next/dynamic";
import type { ApexOptions } from "apexcharts";
import { useChartTheme } from "./useChartTheme";

const ReactApexChart = dynamic(() => import("react-apexcharts"), { ssr: false });

interface SparklineProps {
  data: number[];
  /** Cor do traço — default brand. */
  color?: string;
  /** Altura em px. */
  height?: number;
  /** Descrição acessível pro leitor de tela. */
  ariaLabel?: string;
}

/** Mini gráfico de linha sem eixos / legendas pra encaixar dentro de KPI cards. */
export function Sparkline({ data, color = "#b026ff", height = 36, ariaLabel }: SparklineProps) {
  const chartTheme = useChartTheme();
  const series = useMemo(() => [{ data: data.map((v) => Number(v.toFixed(2))) }], [data]);

  const options: ApexOptions = useMemo(
    () => ({
      chart: {
        type: "line",
        height,
        sparkline: { enabled: true },
        animations: { enabled: false },
        background: "transparent",
      },
      theme: { mode: chartTheme.isDark ? "dark" : "light" },
      stroke: { curve: "smooth", width: 2.5, lineCap: "round" },
      colors: [color],
      tooltip: { enabled: false },
      // Gradient encorpado — opacity alta no topo dá o "wave" de cor que cria
      // identidade visual no KPI card; fade longo até a base mantém respiro.
      fill: {
        type: "gradient",
        gradient: {
          shadeIntensity: 0.8,
          opacityFrom: 0.85,
          opacityTo: 0.05,
          stops: [0, 60, 100],
        },
      },
      markers: { size: 0 },
    }),
    [color, height, chartTheme.isDark]
  );

  if (!data || data.length < 2) return <div style={{ height }} aria-hidden="true" />;

  return (
    <div role="img" aria-label={ariaLabel ?? "Tendência ao longo do período"}>
      <ReactApexChart options={options} series={series} type="line" height={height} />
    </div>
  );
}
