using FellowCore.Domain.Primitives;

namespace FellowCore.Application.Common.Interfaces;

public interface IDomainEventDispatcher
{
    Task DispatchAsync(IReadOnlyList<IDomainEvent> domainEvents, CancellationToken cancellationToken = default);
}
