using FellowCore.Application.Modules.Ledgers.DTOs;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Ledgers.Interfaces;

public interface ILedgerService
{
    Task<LedgerEntry> AddCreditAsync(Guid tenantId, Guid sellerId, CreateLedgerCreditDto dto);
    Task<LedgerBalanceResponse> GetBalanceAsync(Guid tenantId, Guid sellerId);
    Task<LedgerAccount> DebitSellerAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId);
    Task ReversalCreditAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId);
    Task TransferFundsAsync(Guid tenantId, Guid sellerId, decimal amount);
    Task RecordIncomingFundsAsync(Guid tenantId, Guid sellerId, decimal amount, LedgerAccountType accountType, string description);
    Task RecordDirectChargeFundsAsync(Guid tenantId, Guid sellerId, decimal sellerNetAmount, decimal feeAmount, LedgerAccountType accountType, string description);
    Task HoldDisputeAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId);
    Task ReleaseDisputeAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId);
    /// <summary>L11: Freeze platform fee during dispute (Direct Charge). PLATFORM_FEE → DISPUTE_FEE.</summary>
    Task HoldDisputeFeeAsync(Guid tenantId, decimal feeAmount, string description, string transactionId);
    /// <summary>L11: Release frozen platform fee after dispute won (Direct Charge). DISPUTE_FEE → PLATFORM_FEE.</summary>
    Task ReleaseDisputeFeeAsync(Guid tenantId, decimal feeAmount, string description, string transactionId);
    /// <summary>
    /// H12: Zeroes out the DISPUTE account when a dispute is lost. Debits DISPUTE and credits
    /// PLATFORM_PAYOUT to represent funds paid out to the cardholder via chargeback.
    /// </summary>
    Task SettleDisputeLossAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId);
    Task ReversePlatformFeeAsync(Guid tenantId, decimal feeAmount, string description, string transactionId);
    /// <summary>L11: Settle frozen dispute fee on loss (Direct Charge). DISPUTE_FEE → PLATFORM_PAYOUT.</summary>
    Task SettleDisputeFeeLossAsync(Guid tenantId, decimal feeAmount, string description, string transactionId);
    Task DebitPayoutFeeAsync(Guid tenantId, Guid sellerId, decimal feeAmount, string description, string payoutId);
    Task ReversePayoutFeeAsync(Guid tenantId, Guid sellerId, decimal feeAmount, string description, string payoutId);

    /// <summary>
    /// Transfers funds between sellers (debit source WALLET, credit destination WALLET).
    /// Used by SplitProcessor to move split amounts from primary seller to recipients.
    /// </summary>
    Task TransferBetweenSellersAsync(Guid tenantId, Guid fromSellerId, Guid toSellerId, decimal amount, string description, string transactionId);

    /// <summary>
    /// Credits SPLIT_CLEARING with the net amount on capture when transaction has splits.
    /// The clearing account holds funds until SplitProcessor distributes them.
    /// </summary>
    Task CreditSplitClearingAsync(Guid tenantId, decimal amount, string description, string transactionId);

    /// <summary>
    /// Distributes funds from SPLIT_CLEARING to a seller's WALLET.
    /// Used by SplitProcessor for each recipient (including primary seller's share).
    /// </summary>
    Task DistributeFromClearingAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId, string? idempotencyKey = null);

    /// <summary>
    /// Reverses funds from a seller's WALLET back to SPLIT_CLEARING (used on refund/chargeback before external return).
    /// </summary>
    Task ReturnToClearingAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId);

    /// <summary>
    /// Drains SPLIT_CLEARING to PLATFORM_PAYOUT on refund (money leaving the system).
    /// </summary>
    Task DrainClearingForRefundAsync(Guid tenantId, decimal amount, string description, string transactionId);

    /// <summary>
    /// Records platform margin breakdown: PLATFORM_FEE (gross fee) split into PROVIDER_COST and PLATFORM_MARGIN.
    /// Called on transaction capture to make fee/cost/margin visible in the ledger.
    /// </summary>
    Task RecordPlatformMarginAsync(Guid tenantId, decimal platformFee, decimal providerCost, string description, string transactionId);

    /// <summary>
    /// Reverses platform margin proportionally on refund/chargeback.
    /// Debits PLATFORM_MARGIN and PROVIDER_COST, then debits PLATFORM_FEE to zero out the original entries.
    /// </summary>
    Task ReversePlatformMarginAsync(Guid tenantId, decimal platformFee, decimal providerCost, string description, string transactionId);

    /// <summary>
    /// Adjusts PROVIDER_COST and PLATFORM_MARGIN when actual cost differs from estimated.
    /// Positive adjustment means actual > estimated (cost went up, margin went down).
    /// </summary>
    Task RecordCostAdjustmentAsync(Guid tenantId, decimal adjustment, string description, string transactionId);

    /// <summary>
    /// Cobrança da fee de antecipação automática (Modelo Híbrido modo ADVANCE).
    /// Após RecordIncomingFundsAsync ter creditado FUTURE_RECEIVABLES com o NetAmount
    /// inteiro, este método "ajusta" o repasse:
    ///   - Debita FUTURE_RECEIVABLES do seller pelo valor do fee
    ///   - Credita PLATFORM_MARGIN com o mesmo valor (entries linkadas como contraparte)
    /// Resultado: seller fica com (NetAmount - advanceFee) em FUTURE_RECEIVABLES,
    /// plataforma absorve advanceFee como lucro da antecipação.
    /// </summary>
    Task ChargeAdvanceFeeAsync(Guid tenantId, Guid sellerId, decimal advanceFeeAmount, string description, string transactionId);

    /// <summary>
    /// Reversão da advance fee — chamado em refund total / dispute lost de TXs em modo ADVANCE.
    ///
    /// Comportamento espelhado de <see cref="ChargeAdvanceFeeAsync"/>:
    ///   - ForceDebit PLATFORM_MARGIN pelo advanceFee (margem pode ficar negativa
    ///     se o lucro de outras TXs já foi consumido; é aceitável — alerta ops via critical log)
    ///   - Credit volta no seller (FUTURE_RECEIVABLES se ainda existe, senão WALLET)
    /// Sem isso, o seller é cobrado em duplicidade: fee original + gross refund debit.
    /// </summary>
    Task ReverseAdvanceFeeAsync(Guid tenantId, Guid sellerId, decimal advanceFeeAmount, string description, string transactionId);

    /// <summary>
    /// Ajusta o saldo de um seller pra match com uma fonte externa de verdade
    /// (tipicamente o balance Stripe Connect dele). Debita ou força-debita
    /// WALLET e FUTURE_RECEIVABLES até atingir os valores-alvo; a diferença
    /// vai pra EXTERNAL_FUNDS (positivo = dinheiro existe fora do ledger).
    /// Operação reversível só com SQL — log + ReferenceType "BALANCE_RECONCILE"
    /// garantem rastreabilidade no audit.
    /// </summary>
    Task<LedgerReconcileResult> ReconcileSellerBalanceAsync(
        Guid tenantId,
        Guid sellerId,
        decimal targetAvailable,
        decimal targetPending,
        string reason);
}

public record LedgerReconcileResult(
    decimal WalletAdjustment,
    decimal FutureReceivablesAdjustment,
    decimal TotalWriteOff,
    decimal NewWalletBalance,
    decimal NewFutureReceivablesBalance
);