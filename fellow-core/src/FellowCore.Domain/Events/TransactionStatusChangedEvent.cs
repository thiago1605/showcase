using FellowCore.Domain.Enums;
using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Events;

public sealed record TransactionStatusChangedEvent(
    Guid TransactionId,
    Guid TenantId,
    TransactionStatus OldStatus,
    TransactionStatus NewStatus,
    Guid? SellerId,
    decimal? NetAmount,
    PaymentType PaymentType,
    string? ProviderTxId,
    DateTime OccurredAt) : IDomainEvent
{
    public TransactionStatusChangedEvent(
        Guid transactionId,
        Guid tenantId,
        TransactionStatus oldStatus,
        TransactionStatus newStatus,
        Guid? sellerId,
        decimal? netAmount,
        PaymentType paymentType,
        string? providerTxId)
        : this(transactionId, tenantId, oldStatus, newStatus, sellerId, netAmount, paymentType, providerTxId, DateTime.UtcNow) { }
}
