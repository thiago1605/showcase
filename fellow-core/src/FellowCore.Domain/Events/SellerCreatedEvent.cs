using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Events;

public sealed record SellerCreatedEvent(
    Guid SellerId,
    Guid TenantId,
    string Document,
    string Email,
    DateTime OccurredAt) : IDomainEvent
{
    public SellerCreatedEvent(Guid sellerId, Guid tenantId, string document, string email)
        : this(sellerId, tenantId, document, email, DateTime.UtcNow) { }
}
