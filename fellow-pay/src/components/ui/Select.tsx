"use client";
import React, { useEffect, useId, useRef, useState } from "react";

export interface SelectOption {
  value: string;
  label: string;
  /** Conteúdo opcional renderizado em vez de `label` (ex: badge colorido). */
  render?: React.ReactNode;
  disabled?: boolean;
}

interface SelectProps {
  value: string;
  onChange: (value: string) => void;
  options: SelectOption[];
  placeholder?: string;
  disabled?: boolean;
  /** Permitir limpar (botão "—"). Quando true, value="" é válido. */
  allowClear?: boolean;
  /** Largura do trigger. Default `w-full`. */
  className?: string;
  /** ID externo (label `htmlFor`). */
  id?: string;
  /** ARIA label quando não há `<label htmlFor>`. */
  ariaLabel?: string;
  /**
   * Quando passado, renderiza no estilo "stacked" (label flutuante dentro do
   * container, h-14, bg-gray-50, shadow-sm) — mesmo padrão visual do componente
   * `Input` compartilhado. Sem label, fica no estilo "bare" padrão (border
   * + bg-white) usado em formulários antigos.
   */
  label?: string;
}

/**
 * Dropdown custom com aparência consistente do design system. Substitui o
 * `<select>` nativo (cuja popup é controlada pelo SO/navegador) por um painel
 * estilizado que combina com o resto do admin.
 *
 * Decisões:
 * - Não usa lib (Radix etc) — projeto não tem dep instalada.
 * - Fecha em click-outside, Esc ou seleção.
 * - Suporta navegação por teclado (↑ ↓ Enter Esc Home End).
 * - Dark mode via classes Tailwind.
 * - Não usa portals — painel é absolute relativo ao trigger; suficiente
 *   pros formulários atuais (sem casos de overflow:hidden em containers
 *   ancestrais até hoje).
 */
export function Select({
  value,
  onChange,
  options,
  placeholder = "Selecione…",
  disabled = false,
  allowClear = false,
  className = "",
  id,
  ariaLabel,
  label,
}: SelectProps) {
  const [open, setOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState<number>(-1);
  const wrapperRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const listRef = useRef<HTMLUListElement>(null);
  const reactId = useId();
  const listboxId = `${id ?? reactId}-listbox`;

  const enabledOptions = options.filter((o) => !o.disabled);
  const current = options.find((o) => o.value === value);

  // Click outside fecha o painel.
  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => {
      if (wrapperRef.current && !wrapperRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, [open]);

  // Quando abre, foca o item atual ou o primeiro habilitado.
  useEffect(() => {
    if (!open) return;
    const idx = options.findIndex((o) => o.value === value && !o.disabled);
    setActiveIndex(idx >= 0 ? idx : options.findIndex((o) => !o.disabled));
  }, [open, options, value]);

  // Garante que o item ativo fique visível no scroll.
  useEffect(() => {
    if (!open || activeIndex < 0 || !listRef.current) return;
    const el = listRef.current.querySelectorAll<HTMLElement>("[role='option']")[activeIndex];
    el?.scrollIntoView({ block: "nearest" });
  }, [open, activeIndex]);

  // Foca a listbox quando abre, pra capturar teclas de navegação.
  useEffect(() => {
    if (open) listRef.current?.focus();
  }, [open]);

  const move = (delta: number) => {
    if (options.length === 0) return;
    let next = activeIndex;
    for (let i = 0; i < options.length; i++) {
      next = (next + delta + options.length) % options.length;
      if (!options[next]?.disabled) break;
    }
    setActiveIndex(next);
  };

  const onTriggerKey = (e: React.KeyboardEvent<HTMLButtonElement>) => {
    if (disabled) return;
    if (["ArrowDown", "ArrowUp", "Enter", " "].includes(e.key)) {
      e.preventDefault();
      setOpen(true);
    }
  };

  const onListKey = (e: React.KeyboardEvent<HTMLUListElement>) => {
    if (e.key === "Escape") {
      e.preventDefault();
      setOpen(false);
      triggerRef.current?.focus();
    } else if (e.key === "ArrowDown") {
      e.preventDefault();
      move(1);
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      move(-1);
    } else if (e.key === "Home") {
      e.preventDefault();
      const idx = options.findIndex((o) => !o.disabled);
      if (idx >= 0) setActiveIndex(idx);
    } else if (e.key === "End") {
      e.preventDefault();
      let idx = options.length - 1;
      while (idx >= 0 && options[idx].disabled) idx--;
      if (idx >= 0) setActiveIndex(idx);
    } else if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      const opt = options[activeIndex];
      if (opt && !opt.disabled) {
        onChange(opt.value);
        setOpen(false);
        triggerRef.current?.focus();
      }
    } else if (e.key === "Tab") {
      setOpen(false);
    }
  };

  const handleSelect = (opt: SelectOption) => {
    if (opt.disabled) return;
    onChange(opt.value);
    setOpen(false);
    triggerRef.current?.focus();
  };

  // Itens do dropdown — extraídos pra serem reutilizados pelos dois modos
  // (stacked e bare). Mantém o comportamento de seleção/hover idêntico nos dois.
  const listItems = (
    <>
      {allowClear && (
        <li
          role="option"
          aria-selected={value === ""}
          onMouseEnter={() => setActiveIndex(-1)}
          onClick={() => handleSelect({ value: "", label: placeholder })}
          className={`flex items-center gap-2 px-3 py-2 text-sm cursor-pointer text-gray-500 dark:text-gray-400 ${
            activeIndex === -1 ? "bg-gray-50 dark:bg-gray-800" : ""
          } hover:bg-gray-50 dark:hover:bg-gray-800`}
        >
          <span className="w-4" aria-hidden="true" />
          <span>{placeholder}</span>
        </li>
      )}
      {options.map((opt, i) => {
        const selected = opt.value === value;
        const active = i === activeIndex;
        return (
          <li
            key={opt.value || `__${i}`}
            role="option"
            aria-selected={selected}
            aria-disabled={opt.disabled || undefined}
            onMouseEnter={() => !opt.disabled && setActiveIndex(i)}
            onClick={() => handleSelect(opt)}
            className={`flex items-center gap-2 px-3 py-2 text-sm ${
              opt.disabled
                ? "cursor-not-allowed text-gray-400 dark:text-gray-600"
                : "cursor-pointer text-gray-900 dark:text-white"
            } ${active && !opt.disabled ? "bg-gray-50 dark:bg-gray-800" : ""} ${
              selected ? "font-medium" : ""
            } hover:bg-gray-50 dark:hover:bg-gray-800`}
          >
            <span className="w-4 shrink-0 text-brand-500" aria-hidden="true">
              {selected ? (
                <svg width="14" height="14" viewBox="0 0 16 16" fill="none">
                  <path d="M3.5 8.5L6.5 11.5L12.5 5" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              ) : null}
            </span>
            <span className="flex-1">{opt.render ?? opt.label}</span>
          </li>
        );
      })}
    </>
  );

  // Modo stacked: container h-14 com label flutuante. Mesmo padrão visual do
  // <Input> compartilhado (InputField.tsx) — bg-gray-50 + shadow-sm + border
  // sutil + label text-[12px] no topo + valor text-[14px] embaixo. Sem label,
  // mantém o estilo "bare" antigo pra não quebrar outros usos.
  if (label) {
    const containerBorder = disabled
      ? "border-gray-200/80 dark:border-gray-800 opacity-60"
      : "border-gray-200/80 dark:border-gray-800 focus-within:border-brand-500 dark:focus-within:border-brand-500";
    const labelColor = disabled
      ? "text-gray-400"
      : "text-gray-500 dark:text-gray-400 group-focus-within:text-brand-600 dark:group-focus-within:text-brand-400";

    return (
      <div ref={wrapperRef} className={`relative ${className}`}>
        <div
          className={`group relative h-14 flex flex-col justify-center rounded-lg bg-white dark:bg-gray-900/60 px-3 transition-colors border ${containerBorder} ${
            disabled ? "cursor-not-allowed" : ""
          }`}
        >
          <label className={`block text-[12px] font-light transition-colors ${labelColor}`}>{label}</label>
          <button
            ref={triggerRef}
            id={id}
            type="button"
            aria-haspopup="listbox"
            aria-expanded={open}
            aria-controls={listboxId}
            aria-label={ariaLabel}
            disabled={disabled}
            onClick={() => !disabled && setOpen((v) => !v)}
            onKeyDown={onTriggerKey}
            className="w-full flex items-center justify-between gap-2 bg-transparent text-[14px] font-light text-gray-900 dark:text-white text-left focus:outline-none focus:ring-0 disabled:cursor-not-allowed pr-7"
          >
            <span className={current ? "truncate" : "text-gray-400 dark:text-gray-500 truncate"}>
              {current ? current.render ?? current.label : placeholder}
            </span>
          </button>
          <svg
            width="16"
            height="16"
            viewBox="0 0 16 16"
            fill="none"
            aria-hidden="true"
            className={`absolute right-3 top-1/2 -translate-y-1/2 transition-transform shrink-0 pointer-events-none ${open ? "rotate-180" : ""} ${
              disabled ? "text-gray-400" : "text-gray-500 dark:text-gray-400"
            }`}
          >
            <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </div>
        {open && !disabled && (
          <ul
            ref={listRef}
            id={listboxId}
            role="listbox"
            tabIndex={-1}
            onKeyDown={onListKey}
            className="dropdown-in glass-popover absolute z-50 mt-1 w-full max-h-64 overflow-auto rounded-lg py-1 focus:outline-none"
          >
            {listItems}
          </ul>
        )}
      </div>
    );
  }

  return (
    <div ref={wrapperRef} className={`relative ${className}`}>
      <button
        ref={triggerRef}
        id={id}
        type="button"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={listboxId}
        aria-label={ariaLabel}
        disabled={disabled}
        onClick={() => !disabled && setOpen((v) => !v)}
        onKeyDown={onTriggerKey}
        className={`w-full h-11 flex items-center justify-between gap-2 rounded-lg border px-3.5 text-sm font-light text-left transition-colors ${
          disabled
            ? "cursor-not-allowed bg-gray-50 text-gray-400 border-gray-200/80 dark:bg-gray-900/60 dark:text-gray-500 dark:border-gray-800"
            : "bg-white text-gray-900 border-gray-200/80 hover:border-gray-300 focus:border-brand-500 focus:outline-none focus:ring-0 dark:bg-gray-900/60 dark:text-white dark:border-gray-800 dark:hover:border-gray-700 dark:focus:border-brand-500"
        }`}
      >
        <span className={current ? "" : "text-gray-400 dark:text-gray-500"}>
          {current ? current.render ?? current.label : placeholder}
        </span>
        <svg
          width="16"
          height="16"
          viewBox="0 0 16 16"
          fill="none"
          aria-hidden="true"
          className={`transition-transform shrink-0 ${open ? "rotate-180" : ""} ${
            disabled ? "text-gray-400" : "text-gray-500 dark:text-gray-400"
          }`}
        >
          <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>

      {open && !disabled && (
        <ul
          ref={listRef}
          id={listboxId}
          role="listbox"
          tabIndex={-1}
          onKeyDown={onListKey}
          className="dropdown-in glass-popover absolute z-50 mt-1 w-full max-h-64 overflow-auto rounded-lg py-1 focus:outline-none"
        >
          {listItems}
        </ul>
      )}
    </div>
  );
}
