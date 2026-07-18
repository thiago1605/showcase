using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Events;

public class DomainEventDispatcher(IServiceProvider serviceProvider, ILogger<DomainEventDispatcher> logger) : IDomainEventDispatcher
{
    public async Task DispatchAsync(IReadOnlyList<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        foreach (var domainEvent in domainEvents)
        {
            var eventType = domainEvent.GetType();
            var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
            var handlers = serviceProvider.GetServices(handlerType);

            var dispatched = false;

            foreach (var handler in handlers)
            {
                if (handler == null) continue;

                var method = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync));
                if (method == null) continue;

                await (Task)method.Invoke(handler, [domainEvent, cancellationToken])!;
                dispatched = true;
            }

            if (!dispatched)
                logger.LogDebug("Nenhum handler registrado para o evento {EventType}", eventType.Name);
        }
    }
}
