namespace FellowCore.Domain.Primitives;

public interface IDomainEvent
{
    DateTime OccurredAt { get; }
}
