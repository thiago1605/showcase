using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Events;

public sealed record TenantCreatedEvent(
    Guid TenantId,
    string Name,
    string Slug,
    string? OwnerEmail,
    DateTime OccurredAt) : IDomainEvent
{
    public TenantCreatedEvent(Guid tenantId, string name, string slug, string? ownerEmail = null)
        : this(tenantId, name, slug, ownerEmail, DateTime.UtcNow) { }
}
