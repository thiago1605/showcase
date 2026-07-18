import React from "react";

interface SkeletonProps {
  className?: string;
  /** Para divs com altura/largura custom (ex: `h-4 w-32`). */
  style?: React.CSSProperties;
}

/**
 * Skeleton primitivo: bloco com `animate-pulse` em tom neutro. Dimensões via
 * Tailwind no `className` do call-site (ex: `<Skeleton className="h-4 w-32" />`).
 * Sem semantica — somente visual. Para acessibilidade do estado de carregamento,
 * envolva o conjunto num container com `role="status"` + `aria-live="polite"`.
 */
export function Skeleton({ className = "", style }: SkeletonProps) {
  return (
    <div
      aria-hidden="true"
      style={style}
      className={`animate-pulse rounded bg-gray-200 dark:bg-gray-800 ${className}`}
    />
  );
}

/**
 * Layout padrão de skeleton para páginas de detalhe (PaymentLink, Transaction,
 * Refund, Subscription, Payout). Header (título + ação) + 2 cards com linhas.
 * Mantém shape consistente com a UI real pra não causar layout shift quando os
 * dados chegam.
 */
export function DetailPageSkeleton({ ariaLabel = "Carregando detalhes" }: { ariaLabel?: string }) {
  return (
    <div className="space-y-6" role="status" aria-live="polite" aria-label={ariaLabel}>
      <div className="flex items-center justify-between">
        <Skeleton className="h-7 w-64" />
        <Skeleton className="h-9 w-28" />
      </div>
      <div className="rounded-3xl border border-gray-200/60 bg-white p-5 dark:border-gray-800 dark:bg-gray-900 space-y-3">
        <Skeleton className="h-4 w-1/3" />
        <Skeleton className="h-4 w-2/3" />
        <Skeleton className="h-4 w-1/2" />
        <Skeleton className="h-4 w-3/5" />
      </div>
      <div className="rounded-3xl border border-gray-200/60 bg-white p-5 dark:border-gray-800 dark:bg-gray-900 space-y-3">
        <Skeleton className="h-4 w-1/4" />
        <Skeleton className="h-4 w-3/4" />
      </div>
    </div>
  );
}

/**
 * Skeleton de lista em cards (Team, SplitRules, Webhooks, Reports). N cards
 * com titulo + 2 linhas auxiliares.
 */
export function CardListSkeleton({
  count = 4,
  ariaLabel = "Carregando lista",
}: {
  count?: number;
  ariaLabel?: string;
}) {
  return (
    <div className="space-y-3" role="status" aria-live="polite" aria-label={ariaLabel}>
      {Array.from({ length: count }).map((_, i) => (
        <div key={i} className="rounded-3xl border border-gray-200/60 bg-white p-5 dark:border-gray-800 dark:bg-gray-900 space-y-3">
          <div className="flex items-center justify-between">
            <Skeleton className="h-4 w-1/3" />
            <Skeleton className="h-6 w-20 rounded-full" />
          </div>
          <Skeleton className="h-3 w-2/3" />
          <Skeleton className="h-3 w-1/2" />
        </div>
      ))}
    </div>
  );
}
