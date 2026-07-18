using FellowCore.Domain.Enums;
using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Events;

public sealed record TransactionCreatedEvent(
    Guid TransactionId,
    Guid TenantId,
    decimal Amount,
    PaymentType PaymentType,
    PaymentProvider Provider,
    DateTime OccurredAt) : IDomainEvent
{
    public TransactionCreatedEvent(Guid transactionId, Guid tenantId, decimal amount, PaymentType paymentType, PaymentProvider provider)
        : this(transactionId, tenantId, amount, paymentType, provider, DateTime.UtcNow) { }
}
