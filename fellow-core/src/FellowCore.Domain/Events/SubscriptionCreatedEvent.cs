using FellowCore.Domain.Enums;
using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Events;

public sealed record SubscriptionCreatedEvent(
    Guid SubscriptionId,
    Guid TenantId,
    Guid SellerId,
    decimal Amount,
    BillingInterval Interval,
    DateTime OccurredAt) : IDomainEvent
{
    public SubscriptionCreatedEvent(Guid subscriptionId, Guid tenantId, Guid sellerId, decimal amount, BillingInterval interval)
        : this(subscriptionId, tenantId, sellerId, amount, interval, DateTime.UtcNow) { }
}
