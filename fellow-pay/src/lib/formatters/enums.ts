// Centralized enum formatters for the seller portal.
//
// FellowCore serializes enums as integers (System.Text.Json default). Frontend types are
// declared as union strings (e.g. TransactionStatus = "CREATED" | "PROCESSING" | …) which
// matches the *name* of the enum. To stay resilient to either representation — and avoid
// breaking the public B2B contract by flipping the backend serializer — every helper here
// accepts `number | string | null | undefined` and resolves to the canonical KEY (string)
// or a human-friendly LABEL (Portuguese).
//
// Source of truth for indexes: fellow-core/src/FellowCore.Domain/Enums/FellowPayEnums.cs.
// Order in each tuple MUST match the enum's declaration order in C# (zero-based).

const PAYMENT_TYPE = ["CREDIT_CARD", "DEBIT_CARD", "PIX", "BOLETO"] as const;
const PAYMENT_TYPE_LABELS: Record<string, string> = {
  CREDIT_CARD: "Cartão de crédito",
  DEBIT_CARD: "Cartão de débito",
  PIX: "Pix",
  BOLETO: "Boleto",
};

const TRANSACTION_STATUS = [
  "CREATED",
  "PROCESSING",
  "AUTHORIZED",
  "CAPTURED",
  "DECLINED",
  "VOIDED",
  "REFUNDED",
  "CHARGEBACKERROR",
  "FAILED",
] as const;
const TRANSACTION_STATUS_LABELS: Record<string, string> = {
  CREATED: "Criada",
  PROCESSING: "Processando",
  AUTHORIZED: "Autorizada",
  CAPTURED: "Aprovada",
  DECLINED: "Recusada",
  VOIDED: "Cancelada",
  REFUNDED: "Reembolsada",
  CHARGEBACKERROR: "Chargeback",
  FAILED: "Falhou",
};

const PAYOUT_STATUS = ["PENDING", "PROCESSING", "PAID", "FAILED", "CANCELED"] as const;
const PAYOUT_STATUS_LABELS: Record<string, string> = {
  PENDING: "Pendente",
  PROCESSING: "Processando",
  PAID: "Pago",
  FAILED: "Falhou",
  CANCELED: "Cancelado",
};

const SUBSCRIPTION_STATUS = ["ACTIVE", "PAUSED", "CANCELED", "EXPIRED"] as const;
const SUBSCRIPTION_STATUS_LABELS: Record<string, string> = {
  ACTIVE: "Ativa",
  PAUSED: "Pausada",
  CANCELED: "Cancelada",
  EXPIRED: "Expirada",
};

const BILLING_INTERVAL = ["WEEKLY", "MONTHLY", "QUARTERLY", "YEARLY"] as const;
const BILLING_INTERVAL_LABELS: Record<string, string> = {
  WEEKLY: "Semanal",
  MONTHLY: "Mensal",
  QUARTERLY: "Trimestral",
  YEARLY: "Anual",
};

const REFUND_INTENT_STATUS = ["PENDING", "PROCESSING", "COMPLETED", "FAILED"] as const;
const REFUND_INTENT_STATUS_LABELS: Record<string, string> = {
  PENDING: "Pendente",
  PROCESSING: "Processando",
  COMPLETED: "Concluído",
  FAILED: "Falhou",
};

const DISPUTE_STATUS = ["OPEN", "WON", "LOST"] as const;
const DISPUTE_STATUS_LABELS: Record<string, string> = {
  OPEN: "Aberta",
  WON: "Ganha",
  LOST: "Perdida",
};

const DELIVERY_STATUS = ["SUCCEEDED", "FAILED", "PENDING_RETRY"] as const;
const DELIVERY_STATUS_LABELS: Record<string, string> = {
  SUCCEEDED: "Sucesso",
  FAILED: "Falhou",
  PENDING_RETRY: "Aguardando retry",
};

const RECEIPT_TYPE = ["PAYMENT", "REFUND", "PAYOUT", "SPLIT_RECEIVED", "CHARGEBACK"] as const;
const RECEIPT_TYPE_LABELS: Record<string, string> = {
  PAYMENT: "Pagamento",
  REFUND: "Reembolso",
  PAYOUT: "Repasse",
  SPLIT_RECEIVED: "Split recebido",
  CHARGEBACK: "Chargeback",
};

const RECEIPT_STATUS = ["GENERATED", "AVAILABLE", "FAILED"] as const;
const RECEIPT_STATUS_LABELS: Record<string, string> = {
  GENERATED: "Gerado",
  AVAILABLE: "Disponível",
  FAILED: "Falhou",
};

type EnumValue = number | string | null | undefined;

function resolveKey(value: EnumValue, indexed: readonly string[]): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value === "number") {
    return indexed[value] ?? null;
  }
  // String input: trust it if it looks like one of the known keys, otherwise return as-is
  // (defensive — gives the caller a chance to surface raw values during dev).
  return value;
}

function resolveLabel(value: EnumValue, indexed: readonly string[], labels: Record<string, string>, fallback = "—"): string {
  const key = resolveKey(value, indexed);
  if (!key) return fallback;
  return labels[key] ?? key;
}

// ============================================================================
// Public API — one pair (key + label) per enum used by the seller portal.
// ============================================================================

export const paymentTypeKey = (v: EnumValue) => resolveKey(v, PAYMENT_TYPE);
export const paymentTypeLabel = (v: EnumValue) => resolveLabel(v, PAYMENT_TYPE, PAYMENT_TYPE_LABELS);
/**
 * Converts a key (e.g. "PIX") or numeric index back to the integer the backend expects.
 * Use when posting to endpoints that bind enums as int (System.Text.Json default).
 * Returns null when the value can't be resolved (caller decides whether to default).
 */
export function paymentTypeIndex(v: EnumValue): number | null {
  if (v === null || v === undefined) return null;
  if (typeof v === "number") return v;
  const idx = PAYMENT_TYPE.indexOf(v as (typeof PAYMENT_TYPE)[number]);
  return idx >= 0 ? idx : null;
}

const FEE_ALLOCATION_POLICY = ["PRIMARY_SELLER_PAYS_FEES", "PROPORTIONAL_TO_RECIPIENTS", "PLATFORM_ABSORBS"] as const;
/** Converte chave (ex "PLATFORM_ABSORBS") ou índice numérico para o int que o backend espera. */
export function feeAllocationPolicyIndex(v: EnumValue): number | null {
  if (v === null || v === undefined) return null;
  if (typeof v === "number") return v;
  const idx = FEE_ALLOCATION_POLICY.indexOf(v as (typeof FEE_ALLOCATION_POLICY)[number]);
  return idx >= 0 ? idx : null;
}

export const transactionStatusKey = (v: EnumValue) => resolveKey(v, TRANSACTION_STATUS);
export const transactionStatusLabel = (v: EnumValue) => resolveLabel(v, TRANSACTION_STATUS, TRANSACTION_STATUS_LABELS);

export const payoutStatusKey = (v: EnumValue) => resolveKey(v, PAYOUT_STATUS);
export const payoutStatusLabel = (v: EnumValue) => resolveLabel(v, PAYOUT_STATUS, PAYOUT_STATUS_LABELS);

export const subscriptionStatusKey = (v: EnumValue) => resolveKey(v, SUBSCRIPTION_STATUS);
export const subscriptionStatusLabel = (v: EnumValue) => resolveLabel(v, SUBSCRIPTION_STATUS, SUBSCRIPTION_STATUS_LABELS);

export const billingIntervalKey = (v: EnumValue) => resolveKey(v, BILLING_INTERVAL);
export const billingIntervalLabel = (v: EnumValue) => resolveLabel(v, BILLING_INTERVAL, BILLING_INTERVAL_LABELS);

export const refundIntentStatusKey = (v: EnumValue) => resolveKey(v, REFUND_INTENT_STATUS);
export const refundIntentStatusLabel = (v: EnumValue) => resolveLabel(v, REFUND_INTENT_STATUS, REFUND_INTENT_STATUS_LABELS);

export const disputeStatusKey = (v: EnumValue) => resolveKey(v, DISPUTE_STATUS);
export const disputeStatusLabel = (v: EnumValue) => resolveLabel(v, DISPUTE_STATUS, DISPUTE_STATUS_LABELS);

export const deliveryStatusKey = (v: EnumValue) => resolveKey(v, DELIVERY_STATUS);
export const deliveryStatusLabel = (v: EnumValue) => resolveLabel(v, DELIVERY_STATUS, DELIVERY_STATUS_LABELS);

export const receiptTypeKey = (v: EnumValue) => resolveKey(v, RECEIPT_TYPE);
export const receiptTypeLabel = (v: EnumValue) => resolveLabel(v, RECEIPT_TYPE, RECEIPT_TYPE_LABELS);

export const receiptStatusKey = (v: EnumValue) => resolveKey(v, RECEIPT_STATUS);
export const receiptStatusLabel = (v: EnumValue) => resolveLabel(v, RECEIPT_STATUS, RECEIPT_STATUS_LABELS);

const SELLER_STATUS = ["PENDING", "ACTIVE", "SUSPENDED", "BLOCKED"] as const;
const SELLER_STATUS_LABELS: Record<string, string> = {
  PENDING: "Pendente",
  ACTIVE: "Ativa",
  SUSPENDED: "Suspensa",
  BLOCKED: "Bloqueada",
};

export const sellerStatusKey = (v: EnumValue) => resolveKey(v, SELLER_STATUS);
export const sellerStatusLabel = (v: EnumValue) => resolveLabel(v, SELLER_STATUS, SELLER_STATUS_LABELS);

const PAYMENT_PROVIDER = ["STRIPE", "OPENPIX", "SANDBOX"] as const;
const PAYMENT_PROVIDER_LABELS: Record<string, string> = {
  STRIPE: "Stripe",
  OPENPIX: "OpenPix",
  SANDBOX: "Sandbox",
};

export const paymentProviderKey = (v: EnumValue) => resolveKey(v, PAYMENT_PROVIDER);
export const paymentProviderLabel = (v: EnumValue) => resolveLabel(v, PAYMENT_PROVIDER, PAYMENT_PROVIDER_LABELS);
