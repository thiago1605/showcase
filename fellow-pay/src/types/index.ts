// Fellow Pay TypeScript interfaces - Seller Portal scope
// The sellerId is always inferred from the authenticated user's JWT.
// Never allow the frontend to select arbitrary sellerIds.

// Enums
export type TransactionStatus = "CREATED" | "PROCESSING" | "AUTHORIZED" | "CAPTURED" | "DECLINED" | "VOIDED" | "REFUNDED" | "CHARGEBACKERROR" | "FAILED";
export type PaymentType = "CREDIT_CARD" | "DEBIT_CARD" | "PIX" | "BOLETO";
export type PayoutStatus = "PENDING" | "PROCESSING" | "PAID" | "FAILED" | "CANCELED";
export type SubscriptionStatus = "ACTIVE" | "PAUSED" | "CANCELED" | "EXPIRED";
export type BillingInterval = "WEEKLY" | "MONTHLY" | "QUARTERLY" | "YEARLY";
export type DeliveryStatus = "SUCCEEDED" | "FAILED" | "PENDING_RETRY";
export type UserRole = "OWNER" | "DEVELOPER" | "FINANCE" | "VIEWER" | "SUPPORT";
export type DisputeStatus = "OPEN" | "WON" | "LOST";
export type RefundIntentStatus = "PENDING" | "PROCESSING" | "COMPLETED" | "FAILED";
export type SplitTransferStatus = "PENDING" | "RESERVED" | "PROCESSING" | "PAID" | "FAILED" | "REVERSED" | "PARTIALLY_REVERSED";
export type FeeAllocationPolicy = "PRIMARY_SELLER_PAYS_FEES" | "PROPORTIONAL_TO_RECIPIENTS" | "PLATFORM_ABSORBS";

// Auth
export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  accessToken: string;
  refreshToken: string;
  userId: string;
  requiresMfa: boolean;
  mfaToken?: string;
}

export interface TokenResponse {
  accessToken: string;
  refreshToken: string;
  userId: string;
}

// Seller Balance (own account)
export interface SellerBalance {
  total: number;
  blocked: number;
  available: number;
  isAccountReady: boolean;
  /**
   * Breakdown do saldo bloqueado por data prevista de liberação.
   * Agregado por dia no backend — independe de quantas TXs estão por trás.
   * Vazio/ausente quando `blocked === 0`.
   */
  blockedByDate?: SellerReleaseSlot[];
  /**
   * Buckets cumulativos (cada um inclui os anteriores). Cobre os settlement
   * windows reais: débito (D+2), crédito à vista (D+30), crédito 3x/6x/12x.
   */
  blockedBuckets?: SellerReleaseBuckets;
}

export interface SellerReleaseSlot {
  releaseDate: string; // ISO 8601
  amount: number;
}

export interface SellerReleaseBuckets {
  next2Days: number;
  next7Days: number;
  next30Days: number;
  next90Days: number;
  next180Days: number;
  next365Days: number;
}

// Seller Profile (own data) — espelha SellerDetailDto do backend.
export interface SellerProfile {
  id: string;
  legalName: string;
  tradeName: string | null;
  document: string;
  email: string;
  mobilePhone: string | null;
  pixKey: string | null;
  status: string | number;
  preferredProvider: string | number | null;
  externalAccountId: string | null;
  createdAt: string;
  updatedAt: string;
  // --- Modelo Híbrido (advance settlement) ---
  /** Quando true, TXs de crédito viram 1 parcela D+30. Cobra advance fee do plano. */
  autoAdvanceSettlement?: boolean;
  /** Teto de antecipação aprovado pra este seller (R$). 0 = sem permissão de antecipar. */
  advanceCreditLimit?: number;
  /** Quanto ainda está adiantado e não recuperado da Stripe (R$). */
  advanceExposureCurrent?: number;
  // --- Founding Seller (Sprint 0) ---
  /** Marca "Founding Seller" — um dos primeiros membros do ecossistema. */
  isFoundingSeller?: boolean;
  /** Ordinal Founding (#1, #2, ...). Null quando não é Founding. */
  foundingNumber?: number | null;
}

/**
 * Tier de performance do seller. Substitui o conceito de "plano comercial" —
 * pricing é função pura do tier vigente. Tier sobe automaticamente com TPV,
 * sob cooldown. INFINITE é convite exclusivo (admin override).
 */
export type SellerTierCode =
  | "SILVER"
  | "GOLD"
  | "DIAMOND"
  | "BLACK"
  | "INFINITE";

/**
 * Resposta de GET /api/v1/sellers/me/tier.
 * - Quando o seller tem profile persistido: tpv30dBrl é o snapshot do job
 *   mensal (TPV90d na verdade — nome mantido por compatibilidade com fallback).
 * - Quando não tem profile: tpv30dBrl é o TPV30d on-the-fly.
 * Frontend não precisa distinguir — ambos são "TPV recente que define o tier".
 */
export interface SellerTier {
  currentTier: SellerTierCode;
  /** TPV considerado na atribuição do tier (R$). */
  tpv30dBrl: number;
  /** Próximo tier alcançável. Null = já está em BLACK ou INFINITE. */
  nextTier: SellerTierCode | null;
  /** Falta de TPV pra atingir o próximo tier (R$). Null quando nextTier=null. */
  gapToNextBrl: number | null;
  /** Marca Founding (ortogonal a tier). */
  isFoundingSeller: boolean;
  /** Ordinal Founding. Null quando isFoundingSeller=false. */
  foundingNumber: number | null;
}

/** Campos opcionais — qualquer combinação pode ser enviada num PATCH /sellers/me. */
export interface UpdateSellerProfileRequest {
  tradeName?: string | null;
  email?: string | null;
  mobilePhone?: string | null;
  pixKey?: string | null;
  webhookUrl?: string | null;
  /** Liga/desliga antecipação automática (Modelo Híbrido). Só afeta TXs futuras. */
  autoAdvanceSettlement?: boolean | null;
}

// Transactions (seller's own)
// Status/PaymentType chegam como int do backend (System.Text.Json default) — usar
// os formatters (`transactionStatusKey/Label`) pra exibir/comparar.
export interface Transaction {
  id: string;
  amount: number;
  /** Legacy "seller fee" — pode vir 0 se o seller usa PricingPlan moderno.
   *  Pra exibir a taxa REAL paga, prefira `platformFeeAmount`. */
  feeAmount: number;
  netAmount: number;
  refundedAmount: number;
  currency: string;
  status: TransactionStatus | number;
  paymentType: PaymentType | number;
  installments: number;
  description: string;
  createdAt: string;
  updatedAt: string;
  /** Taxa cobrada pela Fellow Pay (vem do PricingPlan). É o "preço" pro seller. */
  platformFeeAmount?: number | null;
  /** Custo que a Fellow Pay paga ao provider (Stripe/OpenPix). Interno — não
   *  expor pro seller. */
  providerCostAmount?: number | null;
  platformMarginAmount?: number | null;
  /** Componente percentual da taxa do plano (ex: 4.89 = 4,89%). Null se o seller
   *  não está em plano ou se o método não tem componente percentual (BOLETO). */
  platformFeeRatePercent?: number | null;
  /** Componente fixo da taxa do plano (ex: 0.49 = R$ 0,49). */
  platformFeeFixedAmount?: number | null;
  /** Código do plano vigente que originou o breakdown (ex: "COMECE"). */
  pricingPlanCode?: string | null;
  /** Pagador — campos planos (alinhados com TransactionDetailDto do backend). */
  payerName?: string | null;
  payerEmail?: string | null;
  payerDocument?: string | null;
  /** Lista de reembolsos (RefundIntents) — vem inline pra render granular do
   *  timeline. Null quando não há nenhum. */
  refunds?: RefundSummary[] | null;
}

/** Resumo de um RefundIntent, alinhado com `RefundSummaryDto` do backend. */
export interface RefundSummary {
  id: string;
  amount: number;
  /** Status do RefundIntent: PENDING | PROCESSING | COMPLETED | FAILED. */
  status: string;
  providerRefundId?: string | null;
  reason?: string | null;
  createdAt: string;
}

// Customers (seller's own)
export interface Customer {
  id: string;
  name: string;
  email: string;
  document: string;
  externalId: string;
  createdAt: string;
}

// Payment Links (seller's own)
export interface PaymentLink {
  id: string;
  token: string;
  url: string;
  amount: number;
  paymentType: PaymentType;
  installments: number;
  description: string;
  /** Lista de métodos aceitos. Sempre ≥1. Para links legacy = [paymentType]. */
  paymentTypes: PaymentType[];
  /** null = ilimitado; UI renderiza como "∞". */
  maxUses: number | null;
  usageCount: number;
  active: boolean;
  expiresAt: string | null;
  createdAt: string;
  sellerId?: string | null;
  /** When set, paying this link generates a transaction with the rule applied. */
  splitRuleId?: string | null;
  /**
   * Modelo Híbrido: override per-link da antecipação automática.
   *   - null/undefined: inherit (TX herda do flag global do seller)
   *   - true: força ADVANCE
   *   - false: força INSTALLMENT
   */
  advanceOptIn?: boolean | null;
}

// Split Rules (seller's own or where seller participates)
export interface SplitRule {
  id: string;
  tenantId: string;
  /** Seller that owns the rule. Null for legacy/tenant-wide rules created via API key. */
  ownerSellerId: string | null;
  /** Display name (TradeName ?? LegalName) do owner. Null se o owner foi removido ou é tenant-wide. */
  ownerSellerName: string | null;
  name: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  recipients: SplitRuleRecipient[];
}

export interface SplitRuleRecipient {
  id: string;
  sellerId: string;
  /** Display name (TradeName ?? LegalName) do recipient. */
  sellerName: string | null;
  percentage: number;
  fixedAmount: number;
  priority: number;
}

// Split Simulation
export interface SimulateSplitRecipient {
  sellerId: string;
  /** Valor fixo em R$ — exclusivo com `percentage`. */
  amount?: number;
  /** Percentual 0-100 — exclusivo com `amount`. */
  percentage?: number;
}

export interface SimulateSplitRequest {
  /** Backend exige — seller cuja simulação está sendo calculada (pricing/provider/etc).
   *  No portal isso vem do JWT (getCurrentSellerId). */
  sellerId: string;
  amount: number;
  paymentType: PaymentType;
  installments?: number;
  /** Modo "regra cadastrada". Exclusivo com `splits`. */
  splitRuleId?: string;
  /** Modo "manual". Exclusivo com `splitRuleId`. */
  splits?: SimulateSplitRecipient[];
  feeAllocationPolicy?: FeeAllocationPolicy;
}

export interface SimulateSplitResponse {
  grossAmount: number;
  platformFee: number;
  providerCostEstimate: number;
  platformMarginEstimate: number;
  netAmount: number;
  recipients: SimulatedRecipient[];
  /** O que sobra pro seller dono depois de descontar splits e taxa. */
  primaryResidual: { sellerId: string; amount: number };
  /** Ajuste em centavos resultante de arredondamento na divisão. */
  roundingAdjustment: number;
  warnings: string[];
}

export interface SimulatedRecipient {
  sellerId: string;
  grossShare: number;
  feeShare: number;
  netShare: number;
  /** "PERCENTAGE" | "FIXED" — o tipo do split que originou. */
  type: string;
}

// Payouts (seller's own)
export interface Payout {
  id: string;
  amount: number;
  fee: number;
  status: PayoutStatus;
  processedAt: string;
  createdAt: string;
}

// Subscriptions (seller's own)
export interface Subscription {
  id: string;
  customerId: string;
  customerName: string;
  amount: number;
  description: string;
  interval: BillingInterval;
  status: SubscriptionStatus;
  nextBillingDate: string;
  cycleCount: number;
  maxCycles: number;
  createdAt: string;
}

// Webhooks (seller's own endpoints)
export interface WebhookEndpoint {
  id: string;
  url: string;
  events: string[];
  enabled: boolean;
  createdAt: string;
  /**
   * Quando setado, é um webhook producer-scoped (do seller). Backend dispara
   * apenas pros eventos cuja TX é do seller correspondente. Quando null, é
   * tenant-wide (legado — visível só pra devs/admin).
   */
  sellerId?: string | null;
}

export interface WebhookDelivery {
  id: string;
  eventType: string;
  responseCode: number;
  success: boolean;
  status: DeliveryStatus;
  retryCount: number;
  createdAt: string;
}

// Team members (seller's team)
export interface TeamMember {
  id: string;
  name: string;
  email: string;
  role: UserRole;
  isTotpEnabled: boolean;
  isActive: boolean;
  lastLogin: string;
  createdAt: string;
}

// Pagination
export interface PaginatedResponse<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

// Notifications — types correspondem ao enum NotificationType no backend
// (positional int 0-10). Adicionar valores aqui é seguro desde que o backend
// suporte o mesmo ordering. Order matters: NÃO reordenar.
export type NotificationTypeCode =
  | "TRANSACTION_CAPTURED"
  | "TRANSACTION_REFUNDED"
  | "DISPUTE_OPENED"
  | "DISPUTE_RESOLVED"
  | "PAYOUT_COMPLETED"
  | "PAYOUT_FAILED"
  | "TIER_UPGRADED"
  | "TIER_DOWNGRADED"
  | "BALANCE_RELEASED"
  | "WEBHOOK_DELIVERY_FAILED"
  | "SYSTEM_ANNOUNCEMENT";

export interface Notification {
  id: string;
  type: NotificationTypeCode;
  title: string;
  body: string;
  resourceUrl: string | null;
  /** Metadata JSON inline — shape varia por type. Frontend trata como unknown
   *  pra evitar discriminated union complexa cedo demais. */
  metadata: Record<string, unknown> | null;
  readAt: string | null;
  createdAt: string;
}

export interface NotificationList {
  items: Notification[];
  totalCount: number;
  unreadCount: number;
}

// Marketplace (Sprint 3 — modelo Kirvano-like) ---------------------------
//
// Enums posicionais — DEVEM bater com a ordem declarada no backend
// (FellowPayEnums.cs). Ordem NÃO pode mudar.

export type ProductTypeCode = "DIGITAL" | "PHYSICAL" | "SERVICE";
export const PRODUCT_TYPE_BY_INDEX: ProductTypeCode[] = [
  "DIGITAL",
  "PHYSICAL",
  "SERVICE",
];

export type AffiliationModeCode = "OPEN" | "REQUEST" | "CLOSED";
export const AFFILIATION_MODE_BY_INDEX: AffiliationModeCode[] = [
  "OPEN",
  "REQUEST",
  "CLOSED",
];

export type ProductStatusCode = "DRAFT" | "PUBLISHED" | "PAUSED" | "ARCHIVED";
export const PRODUCT_STATUS_BY_INDEX: ProductStatusCode[] = [
  "DRAFT",
  "PUBLISHED",
  "PAUSED",
  "ARCHIVED",
];

export type AffiliationStatusCode =
  | "PENDING"
  | "APPROVED"
  | "REJECTED"
  | "REVOKED";
export const AFFILIATION_STATUS_BY_INDEX: AffiliationStatusCode[] = [
  "PENDING",
  "APPROVED",
  "REJECTED",
  "REVOKED",
];

export interface ProductMetrics {
  /** Vendas confirmadas (CAPTURED ou REFUNDED) nos últimos 30d. */
  sales30d: number;
  /** Volume bruto (Amount) das mesmas vendas. */
  volume30d: number;
  /** Afiliados com Status=APPROVED no momento da query (estado, não histórico). */
  activeAffiliates: number;
  /** Sparkline data: 30 ints, índice 0 = 29 dias atrás, 29 = hoje. */
  salesByDay: number[];
}

export interface Product {
  id: string;
  ownerSellerId: string;
  ownerSellerName: string | null;
  name: string;
  slug: string;
  description: string | null;
  coverImageUrl: string | null;
  price: number;
  currency: string;
  type: ProductTypeCode;
  deliveryUrl: string | null;
  defaultAffiliateCommissionPercent: number;
  affiliationMode: AffiliationModeCode;
  status: ProductStatusCode;
  splitRuleId: string | null;
  category: string | null;
  facebookPixelId?: string | null;
  googleAdsConversionId?: string | null;
  createdAt: string;
  updatedAt: string;
  /** Só populado no path /products (listagem do produtor). Null em GET por id,
   *  catálogo e checkout público. */
  metrics?: ProductMetrics | null;
  /** Status da afiliação existente do seller logado para este produto.
   *  Preenchido apenas no catálogo de afiliação (/marketplace/products);
   *  null quando o seller ainda não interagiu com o produto. Permite que o
   *  card mostre "Aguardando aprovação" / "Já afiliado" / "Recusado" sem o
   *  usuário precisar clicar para descobrir. */
  currentSellerAffiliationStatus?: AffiliationStatusCode | null;
}

export interface ProductList {
  items: Product[];
  totalCount: number;
  /** Universo de categorias do catálogo (independente do filtro de categoria
   *  do caller). Preenchido apenas no marketplace catalog; null nos demais. */
  availableCategories?: { name: string; count: number }[] | null;
}

export interface ProductOwnerStats {
  totalProducts: number;
  publishedCount: number;
  draftCount: number;
  pausedCount: number;
  /** Janela em dias (7/30/90) usada nas métricas abaixo. */
  periodDays: number;
  salesInPeriod: number;
  volumeInPeriod: number;
  previousSalesInPeriod: number;
  previousVolumeInPeriod: number;
  salesByDay: number[];
  volumeByDay: number[];
  /** Comissões pagas a afiliados + co-producers (líquido de reversões) na janela. */
  commissionsPaidInPeriod: number;
  previousCommissionsPaidInPeriod: number;
}

export interface Affiliation {
  id: string;
  productId: string;
  productName: string | null;
  productSlug: string | null;
  productPrice: number | null;
  productCoverImageUrl?: string | null;
  affiliateSellerId: string;
  affiliateSellerName: string | null;
  status: AffiliationStatusCode;
  commissionPercent: number | null;
  effectiveCommissionPercent: number;
  trackingCode: string;
  checkoutUrl: string;
  requestedAt: string;
  approvedAt: string | null;
  rejectedAt: string | null;
  revokedAt: string | null;
  rejectedReason: string | null;
  createdAt: string;
}

export interface AffiliationList {
  items: Affiliation[];
  totalCount: number;
}
