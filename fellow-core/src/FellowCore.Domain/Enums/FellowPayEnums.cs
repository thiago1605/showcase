namespace FellowCore.Domain.Enums;

public enum UserRole { SUPER_ADMIN, OWNER, DEVELOPER, FINANCE, VIEWER, SUPPORT }
public enum SellerStatus { PENDING, ACTIVE, SUSPENDED, BLOCKED }
// CANCELED é usado APENAS em TransactionInstallment (refund/dispute LOST cancelam parcelas
// futuras pra elas não dispararem settlement). No nível Transaction o valor não é setado —
// a TX em si vira REFUNDED/VOIDED via TransactionStatus, não por SettlementStatus.
public enum SettlementStatus { PENDING, SETTLED, CANCELED }

/// <summary>
/// Modelo de liquidação aplicado a uma <see cref="Transaction"/> no momento da captura.
///   - INSTALLMENT (default): comportamento clássico marketplace — N parcelas mensais
///     pra crédito Nx, D+30 pra crédito 1x, D+2 pra débito. Plataforma sem risco.
///   - ADVANCE: antecipação automática — 1 única parcela em D+30 com NetAmount inteiro
///     (menos a advance fee). Plataforma adianta o caixa, cobra `AdvancePercentFee`.
/// </summary>
public enum SettlementMode { INSTALLMENT, ADVANCE }
public enum PaymentType { CREDIT_CARD, DEBIT_CARD, PIX, BOLETO }
public enum TransactionStatus { CREATED, PROCESSING, AUTHORIZED, CAPTURED, DECLINED, VOIDED, REFUNDED, CHARGEBACKERROR, FAILED }
public enum SplitStatus { PENDING, SCHEDULED, PAID, FAILED }
public enum SplitTransferStatus { PENDING, RESERVED, PROCESSING, PAID, FAILED, REVERSED, PARTIALLY_REVERSED }
public enum FeeAllocationPolicy { PRIMARY_SELLER_PAYS_FEES, PROPORTIONAL_TO_RECIPIENTS, PLATFORM_ABSORBS }
public enum PaymentProvider { STRIPE, OPENPIX, SANDBOX }
public enum LedgerAccountType { WALLET, FUTURE_RECEIVABLES, DISPUTE, DISPUTE_FEE, PLATFORM_RECEIVABLE, PLATFORM_FEE, PLATFORM_PAYOUT, EXTERNAL_FUNDS, PROVIDER_COST, PLATFORM_MARGIN, SPLIT_CLEARING }
public enum PayoutStatus { PENDING, PROCESSING, PAID, FAILED, CANCELED }
public enum SubscriptionStatus { ACTIVE, PAUSED, CANCELED, EXPIRED }
public enum BillingInterval { WEEKLY, MONTHLY, QUARTERLY, YEARLY }
public enum DeliveryStatus { SUCCEEDED, FAILED, PENDING_RETRY }

/// <summary>
/// Tipo de plano comercial. Substitui os antigos COMECE/CRESCA/SCALA por
/// nomenclatura alinhada com o público-alvo (2026-05-15).
///
///   - INFOPRODUCT: infoprodutores, mentorias, cursos, vendas avulsas. Sem mensalidade.
///   - SAAS_STARTER: SaaS até 500 clientes ativos. R$ 79/mês.
///   - SAAS_GROWTH: SaaS com 500+ clientes ativos. R$ 199/mês.
///   - ENTERPRISE: > R$ 500k/mês. Taxas negociadas individualmente. Sem schedule fixo.
/// </summary>
// Sprint 1.5: enum PlanType deletado — sistema 100% tier-based (SellerTier enum
// abaixo é a fonte única de verdade pra pricing/discount).

/// <summary>
/// Tipo de negócio do seller. Usado pra filtrar quais planos são oferecidos no
/// onboarding e validar migrações entre planos (ex: SaaS exige ≥30% recorrência).
/// </summary>
public enum BusinessType { INFOPRODUCT, SAAS, MARKETPLACE, OTHER }

/// <summary>
/// Tipo de saque: D+0 (instantâneo com taxa 1%) ou D+1 (próximo dia útil, grátis).
/// </summary>
public enum WithdrawType { D0, D1 }
public enum ReportType { TRANSACTIONS, PAYOUTS }
public enum ReportFormat { CSV, PDF }
public enum ReportFrequency { DAILY, WEEKLY, MONTHLY }
public enum PixPaymentStatus { PENDING, PROCESSING, COMPLETED, FAILED }
public enum LoginResult { SUCCESS, INVALID_CREDENTIALS, MFA_REQUIRED, ACCOUNT_LOCKED }
public enum PaymentRailType { STRIPE_CARD, STRIPE_BOLETO, OPENPIX_PIX }

/// <summary>
/// Status de performance do seller. Derivado de TPV mensal (não vendido — é conquistado).
/// Tabela default (Sprint 0):
///   SILVER:  até       R$ 50.000/mês
///   GOLD:    R$ 50k    .. R$ 250.000/mês
///   DIAMOND: R$ 250k   .. R$ 1.000.000/mês
///   BLACK:   R$ 1M     .. R$ 10.000.000/mês
///   INFINITE: convite (admin), nunca auto-atribuído
/// Persistência completa (com cooldown de descida + job mensal) entra na Sprint 1.
/// Por ora, calculado on-the-fly por <c>SellerTierService</c>.
///
/// NOTA: posicional. Renomeado de PLATINUM → DIAMOND mantendo position 2 pra
/// não exigir data migration — Tier column é int no DB, valor 2 segue válido.
/// "Diamond" elimina ambiguidade visual com SILVER (ambos prateados na vida real).
/// </summary>
public enum SellerTier { SILVER, GOLD, DIAMOND, BLACK, INFINITE }
public enum PaymentIntentStatus { PENDING, PROCESSING, CAPTURED, PARTIALLY_REFUNDED, REFUNDED, DISPUTED, CANCELED, FAILED }
public enum DisputeStatus { OPEN, WON, LOST }
public enum StripeChargeMode { DESTINATION_CHARGE, DIRECT_CHARGE }
public enum UsageAttemptStatus { RESERVED, COMPLETED, FAILED }
public enum RefundIntentStatus { PENDING, PROCESSING, COMPLETED, FAILED }

/// <summary>
/// Tipo de notificação in-app entregue ao seller. Adicionar valores aqui é
/// safe (enum int) — não muda os existentes. Cada tipo casa com um producer
/// no <c>INotificationService</c> que monta o título/body padrão.
///
/// Ordem dos valores é estável (positional) — não reordenar.
/// </summary>
public enum NotificationType
{
    TRANSACTION_CAPTURED,
    TRANSACTION_REFUNDED,
    DISPUTE_OPENED,
    DISPUTE_RESOLVED,
    PAYOUT_COMPLETED,
    PAYOUT_FAILED,
    TIER_UPGRADED,
    TIER_DOWNGRADED,
    BALANCE_RELEASED,
    WEBHOOK_DELIVERY_FAILED,
    SYSTEM_ANNOUNCEMENT,
    AFFILIATION_REQUESTED,
    AFFILIATION_APPROVED,
    AFFILIATION_REJECTED
}

/// <summary>
/// Tipo de produto do marketplace (modelo Kirvano-like).
///  - DIGITAL: entrega via URL/conteúdo após captura (curso, ebook, mentoria)
///  - PHYSICAL: produto físico que requer logística (raro nesse modelo, mas suportado)
///  - SERVICE: serviço com agendamento ou execução manual
/// </summary>
public enum ProductType { DIGITAL, PHYSICAL, SERVICE }

/// <summary>
/// Regime de afiliação do produto.
///  - OPEN: qualquer seller pode afiliar, auto-aprovado (zero fricção, leaderboard ranking)
///  - REQUEST: afiliado solicita, produtor aprova/rejeita manualmente
///  - CLOSED: sem afiliação aberta (produto só do produtor + co-producers via SplitRule)
/// </summary>
public enum AffiliationMode { OPEN, REQUEST, CLOSED }

/// <summary>
/// Lifecycle do produto no catálogo.
///  - DRAFT: ainda sendo configurado, não aparece em marketplace nem aceita checkout
///  - PUBLISHED: ativo, aceita checkout + abre afiliação conforme AffiliationMode
///  - PAUSED: temporariamente desativado pelo produtor (sem novas vendas, mas mantém histórico)
///  - ARCHIVED: terminal — produto removido do catálogo mas preserva histórico/affiliations
/// </summary>
public enum ProductStatus { DRAFT, PUBLISHED, PAUSED, ARCHIVED }

/// <summary>
/// Estado de uma <c>Affiliation</c> (relação afiliado ↔ produto).
///  - PENDING: solicitação aguardando aprovação do produtor (REQUEST mode)
///  - APPROVED: afiliação ativa, link de tracking funciona, splits são pagos
///  - REJECTED: produtor recusou (terminal — afiliado pode pedir de novo criando nova row)
///  - REVOKED: produtor cancelou afiliação previamente aprovada (terminal)
/// </summary>
public enum AffiliationStatus { PENDING, APPROVED, REJECTED, REVOKED }

public enum ReconciliationIssueType
{
    // Transaction-level
    MISSING_IN_STRIPE,
    MISSING_IN_LEDGER,
    AMOUNT_MISMATCH,
    STATUS_MISMATCH,
    REFUND_MISMATCH,
    CURRENCY_MISMATCH,

    // Payout-level
    PAYOUT_MISSING_IN_STRIPE,
    PAYOUT_MISSING_IN_LEDGER,
    PAYOUT_AMOUNT_MISMATCH,
    PAYOUT_STATUS_MISMATCH,

    // Ledger-level
    LEDGER_BALANCE_MISMATCH,
    LEDGER_GLOBAL_IMBALANCE,

    // Platform-level
    PLATFORM_BALANCE_DRIFT,

    // Cross-rail invariants (V2)
    DOUBLE_CAPTURE,
    DISPUTE_ORPHAN,
    REFUND_TOTAL_MISMATCH,

    // Split-level (V3)
    SPLIT_TOTAL_MISMATCH,
    SPLIT_LEDGER_MISSING,
    SPLIT_DUPLICATE_CREDIT,
    SPLIT_REFUND_NOT_REVERSED,

    // Provider cost (V3)
    PROVIDER_COST_MISMATCH,

    // Stuck operations (V4)
    PAYOUT_STUCK_PROCESSING,
    REFUND_STUCK_PROCESSING,
    SPLIT_TRANSFER_STUCK_PROCESSING,

    // Operational maturity (V5)
    SPLIT_CLEARING_NON_ZERO_NO_PENDING,
    LEDGER_ENTRY_DUPLICATE_IDEMPOTENCY,
    SELLER_WALLET_NEGATIVE,
    REFUND_PROVIDER_SUCCESS_LEDGER_MISSING,
    PLATFORM_MARGIN_NEGATIVE,
    DIRECT_CHARGE_WITH_SPLIT_LEGACY
}

public enum SettlementItemType
{
    CHARGE,
    APPLICATION_FEE,
    REFUND,
    DISPUTE,
    PAYOUT,
    ADJUSTMENT,
    TRANSFER
}

public enum ReceiptType { PAYMENT, REFUND, PAYOUT, SPLIT_RECEIVED, CHARGEBACK }
public enum ReceiptStatus { GENERATED, AVAILABLE, FAILED }
public enum FiscalInvoiceStatus { NOT_REQUESTED, PENDING, ISSUED, FAILED_RETRYABLE, FAILED_FINAL, CANCELED }

public enum SettlementItemMatchStatus
{
    PENDING,
    MATCHED,
    MISMATCHED,
    MISSING_INTERNAL,
    MISSING_EXTERNAL
}