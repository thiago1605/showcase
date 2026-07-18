"use client";
import React from "react";
import Link from "next/link";

interface EmptyStateCTAProps {
  title: string;
  description: string;
  /** CTA único (legado). Use `actions` pra oferecer múltiplos caminhos. */
  ctaLabel?: string;
  ctaHref?: string;
  /** Lista de ações alternativas — renderiza cada uma como link com seta. */
  actions?: Array<{ label: string; href: string }>;
  /** Tamanho compacto pra dentro de widgets pequenos. */
  compact?: boolean;
}

/**
 * Estado vazio reutilizável com mensagem orientativa e CTA para a ação que
 * desbloqueia o widget. Quando o seller não tem dados, é melhor empurrar para
 * a próxima ação do que mostrar "—" silencioso. Aceita CTA único (ctaLabel +
 * ctaHref) OU lista de ações (`actions`) — quando ambas presentes, `actions`
 * vence.
 */
export function EmptyStateCTA({
  title,
  description,
  ctaLabel,
  ctaHref,
  actions,
  compact = false,
}: EmptyStateCTAProps) {
  const resolvedActions =
    actions ?? (ctaLabel && ctaHref ? [{ label: ctaLabel, href: ctaHref }] : []);

  return (
    <div className={`text-center ${compact ? "py-4" : "py-8"}`}>
      <p className="text-sm font-medium text-gray-700 dark:text-gray-300">{title}</p>
      <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 max-w-xs mx-auto">
        {description}
      </p>
      {resolvedActions.length > 0 && (
        <div
          className={`mt-3 flex items-center justify-center ${
            resolvedActions.length > 1 ? "flex-col gap-2" : ""
          }`}
        >
          {resolvedActions.map((a) => (
            <Link
              key={a.href}
              href={a.href}
              className="inline-flex items-center gap-1 text-xs font-medium text-brand-600 hover:text-brand-700 dark:text-brand-400 dark:hover:text-brand-300 transition-colors"
            >
              {a.label}
              <svg width="10" height="10" viewBox="0 0 10 10" fill="none" aria-hidden="true">
                <path d="M3 1l4 4-4 4" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}
