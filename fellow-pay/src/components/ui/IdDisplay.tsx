"use client";
import React, { useState } from "react";

interface IdDisplayProps {
  id: string;
  /** Quando informado e id === mineId, mostra `mineLabel` ao invés do ID. */
  mineId?: string | null;
  mineLabel?: string;
  /** Quando true, renderiza um botão "Copiar" inline com feedback. */
  copyable?: boolean;
  /** Classe extra para o wrapper. */
  className?: string;
}

/**
 * Exibe identificadores (UUIDs, txIds) em **uppercase + completo + monospace**.
 *
 * Padrão único pra todos os IDs visíveis na app — antes truncávamos com
 * `slice(0, 8)…` em vários lugares, o que escondia parte do dado e impedia
 * o seller de copiar. Agora o ID inteiro fica visível e copiável.
 *
 * Quando `mineId` é informado e bate com `id`, exibe um label amigável
 * (default: "Você") em vez do ID — útil pra contextos seller/recipients.
 */
export function IdDisplay({
  id,
  mineId,
  mineLabel = "Você",
  copyable = false,
  className = "",
}: IdDisplayProps) {
  const [copied, setCopied] = useState(false);

  if (mineId && id === mineId) {
    return <span className={`text-sm font-medium ${className}`}>{mineLabel}</span>;
  }

  const handleCopy = async (e: React.MouseEvent) => {
    e.stopPropagation();
    try {
      await navigator.clipboard.writeText(id.toUpperCase());
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* silently fail */
    }
  };

  return (
    <span className={`inline-flex items-center gap-1.5 ${className}`}>
      <span
        className="font-mono text-[11px] uppercase tracking-tight text-gray-700 dark:text-gray-300 break-all select-all"
        title={id.toUpperCase()}
      >
        {id.toUpperCase()}
      </span>
      {copyable && (
        <button
          type="button"
          onClick={handleCopy}
          aria-label="Copiar ID"
          className="inline-flex items-center justify-center w-5 h-5 rounded text-gray-400 hover:text-gray-600 hover:bg-gray-100 dark:hover:text-gray-200 dark:hover:bg-gray-800 transition-colors shrink-0"
        >
          {copied ? (
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true">
              <path d="M2.5 6.5l2.5 2.5 4.5-5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          ) : (
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true">
              <rect x="3.5" y="3.5" width="6" height="6" rx="1" stroke="currentColor" strokeWidth="1.2" />
              <path d="M2 7.5V2.5C2 1.95 2.45 1.5 3 1.5H8" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" />
            </svg>
          )}
        </button>
      )}
    </span>
  );
}
