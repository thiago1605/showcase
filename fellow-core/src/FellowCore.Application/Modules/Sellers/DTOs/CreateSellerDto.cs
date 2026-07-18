using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Sellers.DTOs;

public record SellerAddressDto(
    string Street,
    string Number,
    string? Complement,
    string Neighborhood,
    string City,
    string State,
    string ZipCode
);

public record CreateSellerDto(
    string LegalName,
    string? TradeName,
    string Document,
    string Email,
    decimal IncomeValue,
    string BirthDate, // YYYY-MM-DD
    string MobilePhone,
    SellerAddressDto Address,

    decimal? FeeDebit,
    decimal? FeeCreditCash,
    decimal? FeeCreditInstallment,
    decimal? FeePixIn,
    decimal? PayoutFixedFee,
    decimal? PayoutPercentFee,

    // Campos BaaS (account-register OpenPix)
    string? BusinessDescription,
    string? BusinessProduct,
    string? BusinessLifetime,
    string? BusinessGoal,
    List<SellerDocumentDto>? Documents,

    // Campos Stripe Custom Account (KYC)
    SellerBankAccountDto? BankAccount = null,

    // Campos KYC adicionais Stripe BR — opcionais com defaults sensatos pra
    // o seller virar ACTIVE sem follow-up manual. Em produção esses 4 campos
    // devem vir do onboarding UI real; em test mode os defaults funcionam.
    string? Mcc = null,
    string? ProductDescription = null,
    string? PoliticalExposure = null,         // "none" | "existing"
    string? VerificationDocumentToken = null  // file_xxx do Stripe Files
);

public record SellerBankAccountDto(
    string AccountHolderName,
    string AccountHolderType, // individual or company
    string RoutingNumber,     // bank code + branch (e.g. "001-1234")
    string AccountNumber
);

public record SellerDocumentDto(
    string Url,
    string Type // SOCIAL_CONTRACT, ATA, BYLAWS
);

/// <summary>
/// Input pra provisionamento retroativo de Stripe Connect (ou OpenPix subaccount)
/// pra sellers que foram criados sem onboarding completo — tipicamente sellers
/// seedados direto no banco. Reaproveita LegalName/Document/Email do seller
/// existente; só pede os campos KYC adicionais que o gateway exige.
/// </summary>
/// <summary>
/// Input pra POST /sellers/{id}/stripe-reconcile. `Reason` é obrigatório
/// pra ficar registrado no audit log da operação destrutiva.
/// </summary>
public record StripeReconcileRequestDto(string Reason);

public record StripeReconcileResultDto(
    Guid SellerId,
    decimal WalletAdjustment,
    decimal FutureReceivablesAdjustment,
    decimal TotalWriteOff,
    decimal NewLocalAvailable,
    decimal NewLocalPending,
    string Reason
);

/// <summary>
/// Resultado da sincronização entre o ledger interno e a conta Stripe Connect.
/// READ-ONLY: não modifica nada, só reporta a divergência. Útil pra detectar
/// dinheiro fantasma (ex: TXs pré-Connect que ficaram no balance da plataforma
/// mas o ledger creditou no seller).
/// </summary>
public record StripeSyncReportDto(
    Guid SellerId,
    string? ExternalAccountId,
    // Local ledger (Fellow Pay)
    decimal LocalAvailable,
    decimal LocalPending,
    decimal LocalTotal,
    // Stripe real (conta conectada)
    decimal StripeAvailable,
    decimal StripePending,
    decimal StripeTotal,
    // Delta: Local - Stripe. Positivo = fantasma local, negativo = saldo no
    // Stripe não contabilizado no ledger.
    decimal DeltaAvailable,
    decimal DeltaPending,
    decimal DeltaTotal,
    bool HasDiscrepancy,
    string Recommendation
);

public record ProvisionConnectAccountDto(
    string BirthDate,            // YYYY-MM-DD
    string MobilePhone,
    SellerAddressDto Address,
    decimal IncomeValue,
    SellerBankAccountDto? BankAccount = null,

    // Overrides opcionais pros 4 campos KYC adicionais. Quando null, defaults
    // do StripePaymentProvider são aplicados (Mcc=5734, ProductDescription
    // = TradeName/LegalName, PoliticalExposure=none, VerificationDocumentToken
    // = `file_identity_document_success` em test mode).
    string? Mcc = null,
    string? ProductDescription = null,
    string? PoliticalExposure = null,
    string? VerificationDocumentToken = null
);

public record UpdateSellerDto(
    string? TradeName = null,
    string? Email = null,
    string? MobilePhone = null,
    string? PixKey = null,
    string? WebhookUrl = null,
    /// <summary>
    /// Modelo Híbrido: ativa/desativa antecipação automática. true = TODAS as TXs
    /// de crédito viram 1 parcela D+30 (cobra AdvancePercentFee do plano).
    /// false = parcela a parcela mensal. null = não altera o flag atual.
    /// </summary>
    bool? AutoAdvanceSettlement = null
);

public record SellerResponseDto(Guid Id, string LegalName, string Document, SellerStatus Status, DateTime CreatedAt);

public record SellerDetailDto(
    Guid Id,
    string LegalName,
    string? TradeName,
    string Document,
    string Email,
    string? MobilePhone,
    string? PixKey,
    SellerStatus Status,
    PaymentProvider? PreferredProvider,
    string? ExternalAccountId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>Modelo Híbrido: true = TXs de crédito viram 1 parcela D+30 (cobra advance fee).</summary>
    bool AutoAdvanceSettlement = false,
    /// <summary>Teto de antecipação aprovado pra este seller (R$). Drives anti-fraude.</summary>
    decimal AdvanceCreditLimit = 0m,
    /// <summary>Quanto ainda não foi recuperado da Stripe (R$). Drives capture decision (capacity restante = Limit − Exposure).</summary>
    decimal AdvanceExposureCurrent = 0m,
    /// <summary>Marca de "Founding Seller" — um dos primeiros do ecossistema. Badge no portal, settable só por admin.</summary>
    bool IsFoundingSeller = false,
    /// <summary>Ordinal do Founding (#1, #2, ...). Null quando não é Founding.</summary>
    int? FoundingNumber = null
);

/// <summary>
/// Marca/desmarca um seller como Founding. Settable apenas por admin. Não é derivado
/// de TPV — é uma narrativa comercial de exclusividade pros primeiros membros.
/// </summary>
public record SetFoundingSellerDto(
    /// <summary>Ordinal do Founding (#1, #2, ...). Obrigatório quando IsFoundingSeller=true.</summary>
    int? FoundingNumber = null,
    /// <summary>true = marca como Founding; false = remove o status.</summary>
    bool IsFoundingSeller = true
);

public record SellerStatementEntryDto(
    string? EndToEndId,
    decimal Amount,
    string? Time,
    string? Type,
    string? Description
);

public record SellerBalanceDto(
    Guid SellerId,
    decimal Total,
    decimal Blocked,
    decimal Available,
    bool IsAccountReady,
    /// <summary>
    /// Breakdown do saldo bloqueado por data prevista de liberação. Agregado por dia —
    /// se houver 10k TXs liberando dia 15/05, é uma linha só. Vazio quando Blocked=0.
    /// </summary>
    List<SellerReleaseSlotDto>? BlockedByDate = null,
    /// <summary>
    /// Buckets cumulativos pra UI compacta. Cada bucket inclui o anterior:
    /// Next30Days ⊇ Next7Days ⊇ Next2Days. Útil pra renderizar "liberando nos
    /// próximos N dias" sem o frontend ter que fazer a soma.
    /// </summary>
    SellerReleaseBucketsDto? BlockedBuckets = null
);

public record SellerReleaseSlotDto(DateTime ReleaseDate, decimal Amount);

/// <summary>
/// Buckets cumulativos (cada um inclui os anteriores). Cobertura:
///   - Next2Days: débito Stripe (D+2), PIX/boleto via Stripe (D+2)
///   - Next7Days: ainda débito + PIX residual
///   - Next30Days: crédito Stripe à vista (D+30) — primeira parcela
///   - Next90Days: crédito 3x (terceira parcela em D+90)
///   - Next180Days: crédito 6x (sexta parcela em D+180)
///   - Next365Days: crédito 12x (décima segunda parcela em D+360)
/// </summary>
public record SellerReleaseBucketsDto(
    decimal Next2Days,
    decimal Next7Days,
    decimal Next30Days,
    decimal Next90Days,
    decimal Next180Days,
    decimal Next365Days
);

public record SellerWithdrawRequestDto(decimal Amount);

public record SellerWithdrawResponseDto(
    Guid SellerId,
    decimal Amount,
    string? EndToEndId,
    decimal RemainingBalance
);

// --- Subaccount DTOs ---

public record SubAccountDto(
    string Name,
    string PixKey,
    decimal Balance,
    bool WithdrawBlocked
);

public record CreateSubAccountDto(
    string PixKey,
    string Name
);

public record SubAccountCreditDebitDto(
    decimal Amount,
    string? Description = null
);

public record SubAccountCreditDebitResponseDto(
    string PixKey,
    decimal Amount,
    string? Description,
    string? Message
);

public record SubAccountTransferDto(
    decimal Amount,
    string FromPixKey,
    string ToPixKey,
    string? FromPixKeyType = null,
    string? ToPixKeyType = null
);

public record SubAccountTransferResponseDto(
    decimal Amount,
    SubAccountSummaryDto? Origin,
    SubAccountSummaryDto? Destination
);

public record SubAccountSummaryDto(
    string Name,
    string PixKey,
    decimal Balance
);

public record SubAccountWithdrawDto(decimal Amount);

public record SubAccountWithdrawResponseDto(
    string? Status,
    decimal Amount
);

public record SubAccountStatementEntryDto(
    string? Id,
    string? Time,
    string? Description,
    decimal Balance,
    decimal Amount,
    string? Type,
    string? OperationType
);
