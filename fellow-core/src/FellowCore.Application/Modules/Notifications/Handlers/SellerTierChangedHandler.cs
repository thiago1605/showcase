using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Notifications.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Events;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Notifications.Handlers;

/// <summary>
/// Cria notificação in-app quando o tier do seller muda (upgrade/downgrade).
/// O event só dispara pra transições reais (não pra Unchanged/BlockedByFreeze),
/// então qualquer chamada aqui é uma transição efetiva.
///
/// Pareado com <see cref="SellerTierRecomputeProcessor"/> via event dispatcher.
/// Notification.CreateAsync já é fire-and-forget internamente — falha aqui
/// não quebra o job de recalculo.
/// </summary>
public class SellerTierChangedHandler(
    INotificationService notificationService,
    ILogger<SellerTierChangedHandler> logger)
    : IDomainEventHandler<SellerTierChangedEvent>
{
    public async Task HandleAsync(SellerTierChangedEvent domainEvent, CancellationToken cancellationToken = default)
    {
        // Defensivo — só processa transições reais. O dispatcher já filtra,
        // mas reduplicar evita bug se essa garantia mudar.
        if (domainEvent.Transition != SellerTierTransition.Upgraded
            && domainEvent.Transition != SellerTierTransition.Downgraded)
        {
            return;
        }

        logger.LogInformation(
            "[NOTIFICATION] Tier {Transition} seller={SellerId} {From}→{To}",
            domainEvent.Transition,
            domainEvent.SellerId,
            domainEvent.PreviousTier,
            domainEvent.NewTier);

        await notificationService.NotifyTierChangedAsync(
            domainEvent.TenantId,
            domainEvent.SellerId,
            domainEvent.PreviousTier,
            domainEvent.NewTier);
    }
}
