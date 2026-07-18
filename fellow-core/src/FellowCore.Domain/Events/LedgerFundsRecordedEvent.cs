using FellowCore.Domain.Enums;
using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Events;

public sealed record LedgerFundsRecordedEvent(
    Guid AccountId,
    Guid TenantId,
    Guid? SellerId,
    decimal Amount,
    LedgerAccountType AccountType,
    string Description,
    DateTime OccurredAt) : IDomainEvent
{
    public LedgerFundsRecordedEvent(Guid accountId, Guid tenantId, Guid? sellerId, decimal amount, LedgerAccountType accountType, string description)
        : this(accountId, tenantId, sellerId, amount, accountType, description, DateTime.UtcNow) { }
}
