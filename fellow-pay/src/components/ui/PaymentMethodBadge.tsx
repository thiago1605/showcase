import { paymentTypeKey, paymentTypeLabel } from "@/lib/formatters/enums";

// Cores por método. Mantida em sincronia com o checkout público (`/pay/[token]`)
// e com `PaymentMethodChart`. Escolhas:
//   PIX = verde (cor do Bacen Pix)
//   CREDIT_CARD = roxo Fellow (brand)
//   DEBIT_CARD = azul (associação tradicional débito)
//   BOLETO = laranja (associação visual boleto bancário)
// Exportado pra ser reusado em qualquer chip/etiqueta que precise da paleta
// canônica dos métodos (PendingFundsCard, etc.) sem duplicar as classes.
export const METHOD_BADGE_CLASS: Record<string, string> = {
  PIX:         "bg-success-50 text-success-700 dark:bg-success-500/10 dark:text-success-400",
  CREDIT_CARD: "bg-brand-50 text-brand-700 dark:bg-brand-500/10 dark:text-brand-400",
  DEBIT_CARD:  "bg-blue-light-50 text-blue-light-700 dark:bg-blue-light-500/10 dark:text-blue-light-400",
  BOLETO:      "bg-orange-50 text-orange-700 dark:bg-orange-500/10 dark:text-orange-400",
};
export const FALLBACK_BADGE = "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300";

/**
 * Hex tokens equivalentes aos badges acima — usados em superfícies que não
 * aceitam classes Tailwind (gráficos, chart libs). Exporta `solid` (cor
 * principal) pra fills/legendas e `soft` pro background da fatia/tag.
 */
export const PAYMENT_METHOD_COLORS: Record<string, { solid: string; soft: string }> = {
  PIX:         { solid: "#16a34a", soft: "#ecfdf5" },
  CREDIT_CARD: { solid: "#6D4CFF", soft: "#f3f1ff" },
  DEBIT_CARD:  { solid: "#3B82F6", soft: "#eff6ff" },
  BOLETO:      { solid: "#d97706", soft: "#fff7ed" },
};

interface PaymentMethodBadgeProps {
  /** Aceita int do enum (`0`/`2`/...) ou chave string (`"PIX"`/`"CREDIT_CARD"`). */
  type: string | number | null | undefined;
  /** Tamanho visual. `sm` é o default; `xs` pra contextos densos como tabelas. */
  size?: "xs" | "sm";
  className?: string;
}

export function PaymentMethodBadge({ type, size = "sm", className = "" }: PaymentMethodBadgeProps) {
  if (type === null || type === undefined) return <span className="text-gray-400">—</span>;
  const key = paymentTypeKey(type) ?? "";
  const colorClass = METHOD_BADGE_CLASS[key] ?? FALLBACK_BADGE;
  const sizeClass = size === "xs" ? "px-2 py-0.5 text-[10px]" : "px-2 py-0.5 text-xs";
  return (
    <span className={`inline-flex items-center whitespace-nowrap rounded-full font-medium ${sizeClass} ${colorClass} ${className}`.trim()}>
      {paymentTypeLabel(type)}
    </span>
  );
}
