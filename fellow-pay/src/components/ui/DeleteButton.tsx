"use client";
import React from "react";

interface DeleteButtonProps {
  onClick: () => void | Promise<void>;
  /** Texto do botão. Default: "Remover". */
  label?: string;
  /** Confirma antes de chamar `onClick` — mostra `window.confirm` com esta mensagem. */
  confirmMessage?: string;
  size?: "xs" | "sm";
  disabled?: boolean;
  /** Visual: `outlined` (default — borda vermelha + texto) ou `ghost` (só texto sem borda). */
  variant?: "outlined" | "ghost";
  ariaLabel?: string;
}

/**
 * Botão de ação destrutiva (remover/excluir). Estilo destacado em vermelho com
 * ícone de lixeira pra dar peso visual à ação. Suporta confirmação inline via
 * `confirmMessage`.
 */
export function DeleteButton({
  onClick,
  label = "Remover",
  confirmMessage,
  size = "sm",
  disabled = false,
  variant = "outlined",
  ariaLabel,
}: DeleteButtonProps) {
  const handleClick = () => {
    if (confirmMessage && !window.confirm(confirmMessage)) return;
    void onClick();
  };

  const sizeClass = size === "xs" ? "px-2 py-1 text-xs gap-1" : "px-3 py-1.5 text-xs gap-1.5";
  const iconSize = size === "xs" ? 12 : 14;

  const variantClass =
    variant === "ghost"
      ? "text-error-600 hover:bg-error-50 dark:text-error-400 dark:hover:bg-error-500/10"
      : "border border-error-200 text-error-600 hover:bg-error-50 hover:border-error-300 dark:border-error-500/30 dark:text-error-400 dark:hover:bg-error-500/10 dark:hover:border-error-500/50";

  return (
    <button
      type="button"
      onClick={handleClick}
      disabled={disabled}
      aria-label={ariaLabel ?? label}
      className={`inline-flex items-center rounded-lg font-medium transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${sizeClass} ${variantClass}`}
    >
      <svg width={iconSize} height={iconSize} viewBox="0 0 16 16" fill="none" aria-hidden="true">
        <path d="M6 3V2a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1v1m-7 0h10m-9 0v10a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1V3M7 6v6M9 6v6" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
      <span>{label}</span>
    </button>
  );
}
