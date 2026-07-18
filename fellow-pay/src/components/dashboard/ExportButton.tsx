"use client";
import React from "react";
import { useQueryClient } from "@tanstack/react-query";
import { dashboardService, SellerDashboardSummary, DashboardTimeseries } from "@/services/dashboard.service";
import { useDashboardPeriod, toLocalDateString } from "./PeriodContext";
import { transactionStatusLabel, paymentTypeLabel } from "@/lib/formatters/enums";

function formatCurrency(value: number): string {
  return value.toLocaleString("pt-BR", { style: "currency", currency: "BRL" });
}

// ---------- CSV ----------

function csvEscape(v: string | number): string {
  const s = String(v);
  if (s.includes(",") || s.includes("\"") || s.includes("\n")) {
    return `"${s.replace(/"/g, '""')}"`;
  }
  return s;
}

function buildCsv(summary: SellerDashboardSummary, timeseries: DashboardTimeseries | null, period: { from: string; to: string }): string {
  const lines: string[] = [];

  lines.push(`Relatório Fellow Pay`);
  lines.push(`Período,${csvEscape(new Date(period.from).toLocaleString("pt-BR"))},${csvEscape(new Date(period.to).toLocaleString("pt-BR"))}`);
  lines.push("");

  lines.push("KPIs");
  lines.push("Métrica,Valor");
  lines.push(`Volume Bruto (R$),${summary.totalVolume.toFixed(2)}`);
  lines.push(`Receita Líquida (R$),${summary.totalNet.toFixed(2)}`);
  lines.push(`Taxas Pagas (R$),${summary.totalFees.toFixed(2)}`);
  lines.push(`Margem (%),${(summary.marginPercent ?? 0).toFixed(2)}`);
  lines.push(`Transações,${summary.transactionCount}`);
  const ticket = summary.transactionCount > 0 ? summary.totalVolume / summary.transactionCount : 0;
  lines.push(`Ticket Médio (R$),${ticket.toFixed(2)}`);
  lines.push("");

  lines.push("Por Status");
  lines.push("Status,Transações,Volume (R$)");
  for (const s of summary.byStatus) {
    lines.push(`${csvEscape(transactionStatusLabel(s.status))},${s.count},${s.volume.toFixed(2)}`);
  }
  lines.push("");

  lines.push("Por Método de Pagamento");
  lines.push("Método,Transações,Volume (R$)");
  for (const p of summary.byPaymentType) {
    lines.push(`${csvEscape(paymentTypeLabel(p.paymentType))},${p.count},${p.volume.toFixed(2)}`);
  }
  lines.push("");

  if (timeseries && timeseries.points.length > 0) {
    lines.push("Série Temporal");
    lines.push("Data/Hora,Volume (R$),Líquido (R$),Taxas (R$),Margem (R$),Transações");
    for (const point of timeseries.points) {
      lines.push(
        [
          new Date(point.date).toISOString(),
          point.volume.toFixed(2),
          point.net.toFixed(2),
          point.fees.toFixed(2),
          point.margin.toFixed(2),
          point.count,
        ].join(",")
      );
    }
  }

  return lines.join("\n");
}

function downloadBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

function downloadCsv(csv: string, filename: string) {
  // BOM pra Excel reconhecer UTF-8 (acentos no português).
  const blob = new Blob(["﻿" + csv], { type: "text/csv;charset=utf-8" });
  downloadBlob(blob, filename);
}

// ---------- PDF ----------

async function buildPdf(
  summary: SellerDashboardSummary,
  timeseries: DashboardTimeseries | null,
  period: { from: string; to: string }
): Promise<Blob> {
  // Imports dinâmicos pra manter o bundle inicial leve — o PDF só carrega
  // quando o seller clica em "Exportar PDF".
  const [{ default: jsPDF }, autoTableMod] = await Promise.all([
    import("jspdf"),
    import("jspdf-autotable"),
  ]);
  const autoTable = (autoTableMod as { default: (doc: unknown, opts: unknown) => void }).default;

  const doc = new jsPDF({ unit: "pt", format: "a4" });
  const pageWidth = doc.internal.pageSize.getWidth();
  const marginX = 40;
  let cursorY = 50;

  // Cabeçalho
  doc.setFont("helvetica", "bold");
  doc.setFontSize(18);
  doc.setTextColor(31, 41, 55);
  doc.text("Fellow Pay — Relatório", marginX, cursorY);
  cursorY += 22;

  doc.setFont("helvetica", "normal");
  doc.setFontSize(10);
  doc.setTextColor(107, 114, 128);
  const periodStr = `${new Date(period.from).toLocaleString("pt-BR")} → ${new Date(period.to).toLocaleString("pt-BR")}`;
  doc.text(`Período: ${periodStr}`, marginX, cursorY);
  cursorY += 10;
  doc.text(`Gerado em: ${new Date().toLocaleString("pt-BR")}`, marginX, cursorY);
  cursorY += 24;

  // KPIs em tabela 2 colunas
  const ticket = summary.transactionCount > 0 ? summary.totalVolume / summary.transactionCount : 0;
  autoTable(doc, {
    startY: cursorY,
    head: [["KPI", "Valor"]],
    body: [
      ["Volume Bruto", formatCurrency(summary.totalVolume)],
      ["Receita Líquida", formatCurrency(summary.totalNet)],
      ["Taxas Pagas", formatCurrency(summary.totalFees)],
      ["Margem", `${(summary.marginPercent ?? 0).toFixed(2)}%`],
      ["Transações", String(summary.transactionCount)],
      ["Ticket Médio", formatCurrency(ticket)],
    ],
    headStyles: { fillColor: [123, 97, 255], textColor: 255, fontStyle: "bold" },
    styles: { fontSize: 9, cellPadding: 5 },
    margin: { left: marginX, right: marginX },
    tableWidth: (pageWidth - marginX * 2) / 2 - 6,
  });
  // @ts-expect-error - lastAutoTable é injetado por jspdf-autotable
  cursorY = (doc.lastAutoTable?.finalY ?? cursorY) + 18;

  // Por Status
  if (summary.byStatus.length > 0) {
    autoTable(doc, {
      startY: cursorY,
      head: [["Status", "Transações", "Volume"]],
      body: summary.byStatus
        .filter((s) => s.count > 0)
        .map((s) => [transactionStatusLabel(s.status), String(s.count), formatCurrency(s.volume)]),
      headStyles: { fillColor: [123, 97, 255], textColor: 255, fontStyle: "bold" },
      styles: { fontSize: 9, cellPadding: 5 },
      margin: { left: marginX, right: marginX },
    });
    // @ts-expect-error - lastAutoTable é injetado por jspdf-autotable
    cursorY = (doc.lastAutoTable?.finalY ?? cursorY) + 18;
  }

  // Por Método
  if (summary.byPaymentType.length > 0) {
    autoTable(doc, {
      startY: cursorY,
      head: [["Método de Pagamento", "Transações", "Volume"]],
      body: summary.byPaymentType
        .filter((p) => p.count > 0)
        .map((p) => [paymentTypeLabel(p.paymentType), String(p.count), formatCurrency(p.volume)]),
      headStyles: { fillColor: [123, 97, 255], textColor: 255, fontStyle: "bold" },
      styles: { fontSize: 9, cellPadding: 5 },
      margin: { left: marginX, right: marginX },
    });
    // @ts-expect-error - lastAutoTable é injetado por jspdf-autotable
    cursorY = (doc.lastAutoTable?.finalY ?? cursorY) + 18;
  }

  // Série Temporal — só inclui buckets com movimento pra não estourar páginas
  // com zeros que não acrescentam (relatório fica enxuto).
  if (timeseries && timeseries.points.length > 0) {
    const nonEmpty = timeseries.points.filter((p) => p.count > 0);
    if (nonEmpty.length > 0) {
      autoTable(doc, {
        startY: cursorY,
        head: [["Data/Hora", "Volume", "Líquido", "Taxas", "Tx"]],
        body: nonEmpty.map((p) => [
          new Date(p.date).toLocaleString("pt-BR"),
          formatCurrency(p.volume),
          formatCurrency(p.net),
          formatCurrency(p.fees),
          String(p.count),
        ]),
        headStyles: { fillColor: [123, 97, 255], textColor: 255, fontStyle: "bold" },
        styles: { fontSize: 8, cellPadding: 4 },
        margin: { left: marginX, right: marginX },
      });
    }
  }

  // Footer em todas as páginas
  const pageCount = doc.getNumberOfPages();
  for (let i = 1; i <= pageCount; i++) {
    doc.setPage(i);
    doc.setFontSize(8);
    doc.setTextColor(156, 163, 175);
    doc.text(
      `Fellow Pay · página ${i} de ${pageCount}`,
      pageWidth / 2,
      doc.internal.pageSize.getHeight() - 20,
      { align: "center" }
    );
  }

  return doc.output("blob");
}

// ---------- Component ----------

type ExportFormat = "csv" | "pdf";

export function ExportButton() {
  const { period } = useDashboardPeriod();
  const queryClient = useQueryClient();
  const [busyFormat, setBusyFormat] = React.useState<ExportFormat | null>(null);
  const [open, setOpen] = React.useState(false);
  const wrapperRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    if (!open) return;
    const onClick = (e: MouseEvent) => {
      if (!wrapperRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", onClick);
    return () => document.removeEventListener("mousedown", onClick);
  }, [open]);

  const fetchData = async () => {
    const summary =
      queryClient.getQueryData<SellerDashboardSummary>(["dashboard", "summary", period.from, period.to]) ??
      (await dashboardService.getSummary({ from: period.from, to: period.to }));

    const granularity = period.preset === "TODAY" ? "Hour" : undefined;
    const tsKey = ["dashboard", "timeseries", period.from, period.to, period.preset, granularity];
    const timeseries =
      queryClient.getQueryData<DashboardTimeseries>(tsKey) ??
      (await dashboardService.getTimeseries({ from: period.from, to: period.to, granularity }));

    return { summary, timeseries };
  };

  const handleExport = async (format: ExportFormat) => {
    setOpen(false);
    setBusyFormat(format);
    try {
      const { summary, timeseries } = await fetchData();
      const fromSlug = toLocalDateString(period.from);
      const toSlug = toLocalDateString(period.to);
      const baseName = `fellow-pay-dashboard-${fromSlug}-${toSlug}`;

      if (format === "csv") {
        const csv = buildCsv(summary, timeseries, period);
        downloadCsv(csv, `${baseName}.csv`);
      } else {
        const pdf = await buildPdf(summary, timeseries, period);
        downloadBlob(pdf, `${baseName}.pdf`);
      }
    } finally {
      setBusyFormat(null);
    }
  };

  const busy = busyFormat !== null;

  return (
    <div ref={wrapperRef} className="relative">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        disabled={busy}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label="Exportar dados da dashboard"
        title="Exportar"
        className="inline-flex items-center gap-1.5 rounded-lg border border-gray-200 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-200 dark:hover:bg-gray-700 disabled:opacity-50 disabled:cursor-wait transition-colors"
      >
        <svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true">
          <path d="M6 1.5v6m0 0L3.5 5M6 7.5L8.5 5M2 9.5h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        {busy ? `Gerando ${busyFormat?.toUpperCase()}...` : "Exportar"}
        <svg width="8" height="8" viewBox="0 0 8 8" fill="none" aria-hidden="true">
          <path d="M1 3l3 3 3-3" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>

      {open && (
        <div
          role="menu"
          className="dropdown-in glass-popover absolute right-0 top-full mt-1 z-20 min-w-[140px] rounded-lg"
        >
          <button
            type="button"
            role="menuitem"
            onClick={() => handleExport("csv")}
            className="block w-full text-left px-3 py-2 text-xs text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-700 first:rounded-t-lg"
          >
            Exportar CSV
          </button>
          <button
            type="button"
            role="menuitem"
            onClick={() => handleExport("pdf")}
            className="block w-full text-left px-3 py-2 text-xs text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-700 last:rounded-b-lg border-t border-gray-100 dark:border-gray-700"
          >
            Exportar PDF
          </button>
        </div>
      )}
    </div>
  );
}
