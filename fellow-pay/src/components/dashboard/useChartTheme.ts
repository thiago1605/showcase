"use client";
import { useTheme } from "@/context/ThemeContext";

/**
 * Cores derivadas do tema atual pra usar nos ApexOptions. ApexCharts não
 * acompanha CSS variables sozinho — precisa de hex literais. Esse hook
 * encapsula a tabela e devolve um objeto reativo ao tema.
 */
export interface ChartTheme {
  isDark: boolean;
  text: string;        // texto principal (centro do donut, valor do tooltip)
  muted: string;       // labels de eixo, legendas
  grid: string;        // linhas de grid e divisores
  tooltipBg: string;   // fundo do tooltip
  background: string;  // fundo geral (caso o chart precise)
}

const LIGHT: ChartTheme = {
  isDark: false,
  text: "#111827",
  muted: "#6B7280",
  grid: "#E5E7EB",
  tooltipBg: "#FFFFFF",
  background: "#FFFFFF",
};

const DARK: ChartTheme = {
  isDark: true,
  text: "#F3F4F6",
  muted: "#9CA3AF",
  grid: "#374151",
  tooltipBg: "#1F2937",
  background: "#111827",
};

export function useChartTheme(): ChartTheme {
  const { theme } = useTheme();
  return theme === "dark" ? DARK : LIGHT;
}
