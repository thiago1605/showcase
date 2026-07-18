using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Transactions.DTOs;

public record PayerDto(
    string Name,
    string Document,
    string Email,
    string? Phone = null
);

public record SplitDto(
    Guid SellerId,
    decimal? Amount = null,
    decimal? Percentage = null
);

public record CreateTransactionDto(
    Guid? SellerId,
    decimal Amount,
    PaymentType PaymentType,
    int Installments,
    string Description,
    PayerDto Payer,
    string? IdempotencyKey = null,
    string? ExternalReferenceId = null,
    List<SplitDto>? Splits = null,
    Guid? SplitRuleId = null,
    FeeAllocationPolicy? FeeAllocationPolicy = null,
    List<TransactionItemDto>? Items = null,
    /// <summary>
    /// Override por-TX da antecipação automática (Modelo Híbrido):
    ///   - null (default): segue seller.AutoAdvanceSettlement
    ///   - true: força ADVANCE (1 parcela D+30) mesmo se seller não tem flag global
    ///   - false: força INSTALLMENT (parcelas mensais) mesmo se seller tem flag global
    /// Útil pra marketplaces onde o checkout decide caso-a-caso.
    /// Só faz efeito pra crédito; débito/PIX/boleto ignoram (sem settlement delay).
    /// </summary>
    bool? AdvanceOptIn = null,
    /// <summary>
    /// Metadata customizada persistida em Transaction.Metadata (JSON). Usado pelo
    /// checkout público de marketplace pra guardar UTM source/medium/campaign +
    /// referrer + outros tracking codes (gclid, fbclid). Caller é responsável
    /// pela sanitização (max value length ~500 chars). Keys/values são strings
    /// — pra estruturas complexas, serializar antes em JSON string.
    /// </summary>
    Dictionary<string, string>? Metadata = null
);

public record TransactionItemDto(
    string Description,
    int Quantity,
    decimal UnitAmount,
    string? ProductId = null,
    Guid? SellerId = null,
    Guid? SplitRuleId = null
);

public record TransactionResponseDto(
    Guid InternalId,
    TransactionStatus Status,
    decimal Amount,
    GatewayPaymentDetails Payment
);

public record GatewayPaymentDetails(
    string TransactionId,
    string? BoletoUrl = null,
    string? PixQrCode = null,
    string? PixImageUrl = null,
    string? ClientSecret = null,
    string? ConnectedAccountId = null
);

public record TransactionDetailDto(
    Guid Id,
    Guid TenantId,
    Guid? SellerId,
    decimal Amount,
    decimal? FeeAmount,
    decimal? NetAmount,
    decimal RefundedAmount,
    string Currency,
    TransactionStatus Status,
    PaymentType PaymentType,
    PaymentProvider Provider,
    string? ProviderTxId,
    int Installments,
    string? Description,
    string? WalletType,
    string? ExternalReferenceId,
    DateTime? ExpectedSettlementDate,
    SettlementStatus SettlementStatus,
    decimal? PlatformFeeAmount,
    decimal? ProviderCostAmount,
    decimal? PlatformMarginAmount,
    // Breakdown da taxa pra exibição (tooltip de "como calculamos sua taxa").
    // Vem do plano vigente do seller no momento da consulta — se o plano mudou
    // depois da transação, o breakdown pode não bater exatamente com o
    // `PlatformFeeAmount` armazenado, então o frontend deve mostrar `Total`
    // sempre baseado no `PlatformFeeAmount` (verdade) e usar essas duas pra
    // explicar de onde veio. Null pra wallets/legacy sem plano.
    decimal? PlatformFeeRatePercent,
    decimal? PlatformFeeFixedAmount,
    string? PricingPlanCode,
    // Dados do pagador (preenchidos no checkout para card/Pix/Boleto; podem ser
    // null pra wallets como Apple/Google Pay que não fornecem document).
    string? PayerName,
    string? PayerEmail,
    string? PayerDocument,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<TransactionEventDto>? Timeline = null,
    // Lista de reembolsos (RefundIntents) associados à transação. Vem do detalhe
    // pra permitir timeline granular ("Reembolso de R$ X" por item) sem segundo
    // round-trip pro `/api/v1/refunds?transactionId=...`. Null quando não há
    // nenhum reembolso registrado.
    List<RefundSummaryDto>? Refunds = null
);

public record TransactionEventDto(
    TransactionStatus Status,
    DateTime CreatedAt
);

/// <summary>
/// Resumo de um RefundIntent pra exibir no detalhe da transação. Não inclui
/// campos internos (idempotency key, attempt count) — só o que o seller precisa
/// pra entender o histórico.
/// </summary>
public record RefundSummaryDto(
    Guid Id,
    decimal Amount,
    string Status,
    string? ProviderRefundId,
    string? Reason,
    DateTime CreatedAt
);

public record RefundRequestDto(
    decimal? Amount = null,
    string? Reason = null
);

/// <summary>
/// Quebra detalhada do reembolso, usada pra preview no portal antes da
/// confirmação. POLÍTICA: o seller paga o GROSS INTEGRAL — debita da carteira
/// o mesmo valor que o cliente recebe. Sem isso, a plataforma ficava com
/// prejuízo da taxa do provider (Stripe não devolve a taxa deles) E ainda
/// perdia parte da margem em cada refund.
///
/// Conceitos:
///  - <c>CustomerRefund</c>: valor cheio que sai pra cliente (= refundAmount).
///  - <c>SellerTotalDebit</c>: valor que sai da carteira do seller (= refundAmount).
///    Igual ao customer refund — o seller assume integralmente o ônus do estorno.
///  - <c>SellerNetPortion</c>: a fatia do líquido que ele tinha recebido na
///    captura (informativo — pra mostrar quanto ele perde de líquido).
///  - <c>PlatformFeeWithheld</c>: a taxa Fellow Pay que ele já tinha pago e que
///    não é devolvida (informativo). = SellerTotalDebit - SellerNetPortion.
///  - <c>ProviderCostPortion</c>: parte do <c>PlatformFeeWithheld</c> que vai pra
///    Stripe (custo real não recuperável da plataforma). Informativo — não
///    exposto ao seller, mas útil pra reconciliação interna.
/// </summary>
public record RefundBreakdownDto(
    decimal RefundAmount,
    decimal CustomerRefund,
    decimal SellerNetPortion,
    decimal PlatformFeeWithheld,
    decimal ProviderCostPortion,
    decimal SellerTotalDebit,
    decimal MaxRefundableAmount
);

public record RefundResponseDto(
    Guid TransactionId,
    TransactionStatus Status,
    string? RefundId
);

public record RefundDetailDto(
    string CorrelationId,
    string Status,
    decimal Amount,
    string? EndToEndId,
    string? ReturnIdentification,
    string? Time,
    string? Comment
);