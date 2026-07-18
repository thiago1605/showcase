using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Rails;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Transactions.Interfaces;

public interface IPaymentRail
{
    PaymentRailType RailType { get; }
    LedgerAccountType CaptureAccountType { get; }
    LedgerPolicy LedgerPolicy { get; }

    /// <summary>Priority for failover ordering — lower = higher priority.</summary>
    int Priority => 0;

    /// <summary>Whether this rail can serve as a failover target for the given payment type.</summary>
    bool IsFailoverCandidate(PaymentType method) => false;

    bool Supports(PaymentType method);
    void Validate(CreateTransactionDto request);
    (decimal Fee, decimal Net) CalculateFees(decimal amount, PaymentType type, int installments, Seller? seller);
    DateTime CalculateSettlementDate(DateTime capturedAt, PaymentType type, int installments);
    bool CanRefund(Transaction transaction);

    Task<GatewayPaymentDetails> ExecutePaymentAsync(
        Tenant tenant, Seller? seller, CreateTransactionDto request,
        decimal feeAmount, string idempotencyKey,
        Guid? transactionId = null);

    Task<string?> ExecuteRefundAsync(
        Tenant tenant, Seller? seller, string providerTxId, decimal amount, string? reason, string? idempotencyKey);

    Task CancelAsync(Tenant tenant, Seller? seller, string providerTxId);
}
