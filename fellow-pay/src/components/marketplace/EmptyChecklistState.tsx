"use client";

import Link from "next/link";
import { Illustration, type IllustrationName } from "@/components/ui/Illustration";

/**
 * Empty state inteligente pras listas do marketplace. Em vez de "Você ainda
 * não tem X" + 1 botão, mostra um checklist de próximos passos orientando
 * o usuário pelo onboarding natural da feature.
 *
 * Pattern Linear / Notion / Stripe Connect: empty state como guia de
 * descoberta, não como mensagem morta. Cada `step` é uma micro-conquista
 * que o usuário vai destravando ao usar o produto.
 */

export interface ChecklistStep {
  /** Emoji ou ícone curto pra anchor visual. */
  icon: string;
  title: string;
  description: string;
  /** Quando true, mostra como "✓ feito" em cinza riscado. */
  done?: boolean;
  /** Link de ação principal — vira CTA do step. */
  href?: string;
  cta?: string;
}

export function EmptyChecklistState({
  title,
  subtitle,
  steps,
  illustration = "empty-catalog",
}: {
  title: string;
  subtitle?: string;
  steps: ChecklistStep[];
  /** Illustration exibida acima do título. Default empty-catalog. */
  illustration?: IllustrationName;
}) {
  return (
    <div className="rounded-2xl border border-gray-200/80 bg-white dark:border-gray-800 dark:bg-gray-900 p-6 sm:p-8 max-w-2xl mx-auto">
      <div className="text-center mb-6">
        <Illustration name={illustration} size="lg" className="mx-auto mb-4" />
        <p className="text-lg font-semibold text-gray-900 dark:text-white">{title}</p>
        {subtitle && (
          <p className="text-sm text-gray-500 dark:text-gray-400 mt-1.5">{subtitle}</p>
        )}
      </div>
      <ol className="space-y-3">
        {steps.map((step, idx) => (
          <li
            key={idx}
            className={`flex items-start gap-3 rounded-xl px-4 py-3 ${
              step.done
                ? "bg-success-50/50 dark:bg-success-500/5"
                : "bg-gray-50 dark:bg-gray-800/50"
            }`}
          >
            {/* Indicador de step: número + check quando feito. Lado esquerdo
                fixo pra alinhar texto entre steps de tamanhos variados. */}
            <div className={`shrink-0 w-7 h-7 rounded-full flex items-center justify-center text-xs font-bold ${
              step.done
                ? "bg-success-500 text-white"
                : "bg-white dark:bg-gray-900 text-gray-700 dark:text-gray-300 border border-gray-200 dark:border-gray-700"
            }`}>
              {step.done ? "✓" : idx + 1}
            </div>
            <div className="flex-1 min-w-0">
              <div className="flex items-baseline gap-2 flex-wrap">
                <p className={`text-sm font-medium ${
                  step.done
                    ? "text-gray-500 dark:text-gray-400 line-through"
                    : "text-gray-900 dark:text-white"
                }`}>
                  <span className="mr-1.5">{step.icon}</span>
                  {step.title}
                </p>
              </div>
              <p className={`text-xs mt-0.5 ${
                step.done ? "text-gray-400 dark:text-gray-500" : "text-gray-500 dark:text-gray-400"
              }`}>
                {step.description}
              </p>
            </div>
            {step.href && step.cta && !step.done && (
              <Link
                href={step.href}
                className="shrink-0 h-8 inline-flex items-center rounded-lg bg-brand-500 hover:bg-brand-600 px-3 text-xs font-medium text-white whitespace-nowrap"
              >
                {step.cta}
              </Link>
            )}
          </li>
        ))}
      </ol>
    </div>
  );
}
