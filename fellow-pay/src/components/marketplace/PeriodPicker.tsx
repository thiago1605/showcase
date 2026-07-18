"use client";

import React from "react";

/**
 * Period picker pro filtro das stats do marketplace.
 *
 * Aceita qualquer N entre 1 e 365 (backend valida o range, fallback 30).
 * UI: 3 presets de pill (7d/30d/90d) + botão "Mais" que abre um popover
 * com presets extras (15d/60d/180d/365d) e input livre de dias.
 *
 * Usado em `/affiliations/[id]` e `/products`.
 */

export type PeriodDays = number;

const PRIMARY_PRESETS: { value: PeriodDays; label: string }[] = [
  { value: 7, label: "7d" },
  { value: 30, label: "30d" },
  { value: 90, label: "90d" },
];

const SECONDARY_PRESETS: { value: PeriodDays; label: string }[] = [
  { value: 15, label: "15 dias" },
  { value: 60, label: "60 dias" },
  { value: 180, label: "180 dias" },
  { value: 365, label: "365 dias" },
];

export function PeriodPicker({
  value,
  onChange,
  ariaLabel = "Período",
}: {
  value: PeriodDays;
  onChange: (v: PeriodDays) => void;
  ariaLabel?: string;
}) {
  const [open, setOpen] = React.useState(false);
  const [customDays, setCustomDays] = React.useState<string>("");
  const containerRef = React.useRef<HTMLDivElement>(null);

  // Click-outside fecha o popover. Listener no document é leve — adiciona/
  // remove só enquanto open=true.
  React.useEffect(() => {
    if (!open) return;
    function onClick(e: MouseEvent) {
      if (!containerRef.current?.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("mousedown", onClick);
    return () => document.removeEventListener("mousedown", onClick);
  }, [open]);

  // Match exato dos presets primários determina se um chip aparece ativo —
  // valor custom não destaca nenhum dos 3, fica como "Mais (N dias)".
  const isPrimary = PRIMARY_PRESETS.some((p) => p.value === value);

  function applyCustom() {
    const n = parseInt(customDays, 10);
    if (Number.isFinite(n) && n >= 1 && n <= 365) {
      onChange(n);
      setOpen(false);
      setCustomDays("");
    }
  }

  return (
    <div ref={containerRef} className="relative inline-flex" role="group" aria-label={ariaLabel}>
      <div className="inline-flex p-0.5 bg-gray-100 dark:bg-gray-800 rounded-lg">
        {PRIMARY_PRESETS.map((p) => (
          <button
            key={p.value}
            type="button"
            onClick={() => onChange(p.value)}
            aria-pressed={value === p.value}
            className={`h-7 px-3 text-xs font-semibold rounded-md transition-colors tabular-nums ${
              value === p.value
                ? "bg-white dark:bg-gray-900 text-gray-900 dark:text-white shadow-sm"
                : "text-gray-500 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white"
            }`}
          >
            {p.label}
          </button>
        ))}
        <button
          type="button"
          onClick={() => setOpen((o) => !o)}
          aria-pressed={!isPrimary}
          aria-expanded={open}
          aria-haspopup="true"
          className={`h-7 px-3 text-xs font-semibold rounded-md transition-colors tabular-nums inline-flex items-center gap-1 ${
            !isPrimary
              ? "bg-white dark:bg-gray-900 text-gray-900 dark:text-white shadow-sm"
              : "text-gray-500 dark:text-gray-400 hover:text-gray-900 dark:hover:text-white"
          }`}
        >
          {isPrimary ? "Mais" : `${value}d`}
          <svg className="w-3 h-3" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.5">
            <path d="M3 4.5L6 7.5L9 4.5" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </button>
      </div>

      {open && (
        <div className="dropdown-in glass-popover absolute right-0 top-full mt-1.5 z-20 w-56 rounded-lg p-2">
          <p className="text-[10px] uppercase tracking-wider font-semibold text-gray-500 dark:text-gray-400 px-2 mb-1.5">
            Presets
          </p>
          <div className="grid grid-cols-2 gap-1">
            {SECONDARY_PRESETS.map((p) => (
              <button
                key={p.value}
                type="button"
                onClick={() => {
                  onChange(p.value);
                  setOpen(false);
                }}
                aria-pressed={value === p.value}
                className={`h-8 px-2 text-xs font-medium rounded-md tabular-nums text-left transition-colors ${
                  value === p.value
                    ? "bg-brand-50 dark:bg-brand-500/15 text-brand-700 dark:text-brand-300"
                    : "text-gray-700 dark:text-gray-300 hover:bg-gray-50 dark:hover:bg-gray-800"
                }`}
              >
                {p.label}
              </button>
            ))}
          </div>
          <div className="mt-3 pt-3 border-t border-gray-100 dark:border-gray-800">
            <p className="text-[10px] uppercase tracking-wider font-semibold text-gray-500 dark:text-gray-400 px-2 mb-1.5">
              Personalizado
            </p>
            <div className="flex items-center gap-2 px-2">
              <input
                type="number"
                min={1}
                max={365}
                value={customDays}
                onChange={(e) => setCustomDays(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    applyCustom();
                  }
                }}
                placeholder="N"
                className="w-16 h-8 px-2 text-xs text-gray-900 dark:text-white tabular-nums bg-gray-50 dark:bg-gray-800 rounded-md border border-gray-200 dark:border-gray-700 focus:outline-none focus:ring-1 focus:ring-brand-500"
              />
              <span className="text-xs text-gray-500 dark:text-gray-400">dias</span>
              <button
                type="button"
                onClick={applyCustom}
                disabled={
                  !customDays ||
                  !Number.isFinite(parseInt(customDays, 10)) ||
                  parseInt(customDays, 10) < 1 ||
                  parseInt(customDays, 10) > 365
                }
                className="ml-auto h-8 px-3 text-xs font-semibold rounded-md bg-brand-500 hover:bg-brand-600 text-white disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
              >
                Aplicar
              </button>
            </div>
            <p className="text-[10px] text-gray-500 dark:text-gray-400 mt-1.5 px-2">
              Entre 1 e 365 dias
            </p>
          </div>
        </div>
      )}
    </div>
  );
}
