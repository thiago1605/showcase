"use client";
import React, { createContext, useContext, useMemo, useState } from "react";

export type PeriodPreset = "TODAY" | "LAST_7" | "LAST_30" | "LAST_90" | "CUSTOM";

export interface DashboardPeriod {
  preset: PeriodPreset;
  from: string;
  to: string;
  /** Start of the previous period of equal length — used for delta indicators. */
  prevFrom: string;
  prevTo: string;
}

interface PeriodContextValue {
  period: DashboardPeriod;
  setPreset: (p: Exclude<PeriodPreset, "CUSTOM">) => void;
  setCustom: (from: string, to: string) => void;
}

/**
 * Converte um ISO datetime pra "YYYY-MM-DD" no fuso local do usuário. Necessário
 * porque `period.from`/`period.to` são montados em horário local mas serializados
 * em UTC — fazer `.slice(0, 10)` direto pega o dia em UTC e pode dar offset de 1
 * dia (ex: endOfDay BRT → 02:59 UTC do dia seguinte). Usado por qualquer drill-down
 * que precise navegar com `?from=YYYY-MM-DD&to=YYYY-MM-DD` consistente com o que
 * o usuário vê na dashboard.
 */
export function toLocalDateString(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso.slice(0, 10);
  const yyyy = d.getFullYear();
  const mm = String(d.getMonth() + 1).padStart(2, "0");
  const dd = String(d.getDate()).padStart(2, "0");
  return `${yyyy}-${mm}-${dd}`;
}

const PeriodContext = createContext<PeriodContextValue | null>(null);

function startOfDay(d: Date): Date {
  const x = new Date(d);
  x.setHours(0, 0, 0, 0);
  return x;
}
function endOfDay(d: Date): Date {
  const x = new Date(d);
  x.setHours(23, 59, 59, 999);
  return x;
}

function buildPreset(preset: Exclude<PeriodPreset, "CUSTOM">): DashboardPeriod {
  const now = new Date();
  const to = endOfDay(now);
  let days = 30;
  if (preset === "TODAY") days = 1;
  else if (preset === "LAST_7") days = 7;
  else if (preset === "LAST_30") days = 30;
  else if (preset === "LAST_90") days = 90;

  const from = startOfDay(new Date(now.getTime() - (days - 1) * 24 * 60 * 60 * 1000));
  const prevTo = new Date(from.getTime() - 1);
  const prevFrom = startOfDay(new Date(prevTo.getTime() - (days - 1) * 24 * 60 * 60 * 1000));

  return {
    preset,
    from: from.toISOString(),
    to: to.toISOString(),
    prevFrom: prevFrom.toISOString(),
    prevTo: prevTo.toISOString(),
  };
}

function buildCustom(fromIso: string, toIso: string): DashboardPeriod {
  const from = new Date(fromIso);
  const to = new Date(toIso);
  const ms = to.getTime() - from.getTime();
  const prevTo = new Date(from.getTime() - 1);
  const prevFrom = new Date(prevTo.getTime() - ms);
  return {
    preset: "CUSTOM",
    from: from.toISOString(),
    to: to.toISOString(),
    prevFrom: prevFrom.toISOString(),
    prevTo: prevTo.toISOString(),
  };
}

export function DashboardPeriodProvider({ children }: { children: React.ReactNode }) {
  const [period, setPeriod] = useState<DashboardPeriod>(() => buildPreset("LAST_30"));

  const value = useMemo<PeriodContextValue>(
    () => ({
      period,
      setPreset: (p) => setPeriod(buildPreset(p)),
      setCustom: (from, to) => setPeriod(buildCustom(from, to)),
    }),
    [period]
  );

  return <PeriodContext.Provider value={value}>{children}</PeriodContext.Provider>;
}

export function useDashboardPeriod(): PeriodContextValue {
  const ctx = useContext(PeriodContext);
  if (!ctx) throw new Error("useDashboardPeriod must be used inside <DashboardPeriodProvider>");
  return ctx;
}
