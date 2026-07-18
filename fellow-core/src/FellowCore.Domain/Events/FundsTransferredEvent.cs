using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Events;

public sealed record FundsTransferredEvent(
    Guid FromAccountId,
    Guid ToAccountId,
    decimal Amount,
    DateTime OccurredAt) : IDomainEvent
{
    public FundsTransferredEvent(Guid fromAccountId, Guid toAccountId, decimal amount)
        : this(fromAccountId, toAccountId, amount, DateTime.UtcNow) { }
}
