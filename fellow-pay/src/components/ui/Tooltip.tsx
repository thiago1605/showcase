"use client";

import React, { useId, useState } from "react";

interface TooltipProps {
  /** Conteúdo do tooltip — pode ser texto simples ou JSX. */
  content: React.ReactNode;
  /** Elemento que dispara o tooltip (geralmente um ícone ou label). */
  children: React.ReactElement;
  /** Posição relativa ao trigger. */
  side?: "top" | "bottom" | "left" | "right";
  /** Largura máxima do tooltip. Default 220px. */
  maxWidth?: number;
  className?: string;
}

/**
 * Tooltip leve baseado em hover/focus, sem dependência de lib externa. Renderiza
 * acima/abaixo/lado do trigger conforme `side`. Aria-described automaticamente
 * pra acessibilidade. Não é portaled — se o container do trigger tiver
 * `overflow-hidden`, o tooltip pode ser clipado (use `side="bottom"` ou mova
 * pra fora do container).
 */
export function Tooltip({ content, children, side = "top", maxWidth = 220, className = "" }: TooltipProps) {
  const tooltipId = useId();
  const [open, setOpen] = useState(false);

  const positionClasses: Record<NonNullable<TooltipProps["side"]>, string> = {
    top: "bottom-full left-1/2 -translate-x-1/2 mb-2",
    bottom: "top-full left-1/2 -translate-x-1/2 mt-2",
    left: "right-full top-1/2 -translate-y-1/2 mr-2",
    right: "left-full top-1/2 -translate-y-1/2 ml-2",
  };

  // Setinha posicionada na borda oposta ao tooltip pra apontar pro trigger.
  const arrowClasses: Record<NonNullable<TooltipProps["side"]>, string> = {
    top: "top-full left-1/2 -translate-x-1/2 -mt-px border-t-gray-900 dark:border-t-gray-700 border-x-transparent border-b-transparent",
    bottom: "bottom-full left-1/2 -translate-x-1/2 -mb-px border-b-gray-900 dark:border-b-gray-700 border-x-transparent border-t-transparent",
    left: "left-full top-1/2 -translate-y-1/2 -ml-px border-l-gray-900 dark:border-l-gray-700 border-y-transparent border-r-transparent",
    right: "right-full top-1/2 -translate-y-1/2 -mr-px border-r-gray-900 dark:border-r-gray-700 border-y-transparent border-l-transparent",
  };

  const trigger = React.cloneElement(children as React.ReactElement<Record<string, unknown>>, {
    onMouseEnter: () => setOpen(true),
    onMouseLeave: () => setOpen(false),
    onFocus: () => setOpen(true),
    onBlur: () => setOpen(false),
    "aria-describedby": open ? tooltipId : undefined,
  });

  return (
    <span className={`relative inline-flex items-center ${className}`.trim()}>
      {trigger}
      {open && (
        <span
          id={tooltipId}
          role="tooltip"
          className={`pointer-events-none absolute z-50 rounded-md bg-gray-900 px-2.5 py-1.5 text-[11px] font-normal leading-snug text-white shadow-lg dark:bg-gray-700 ${positionClasses[side]}`}
          style={{ maxWidth, width: "max-content" }}
        >
          {content}
          <span
            aria-hidden="true"
            className={`absolute border-4 ${arrowClasses[side]}`}
          />
        </span>
      )}
    </span>
  );
}
