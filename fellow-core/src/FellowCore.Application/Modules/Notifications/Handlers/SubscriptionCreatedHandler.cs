using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Email.Templates;
using FellowCore.Domain.Events;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Notifications.Handlers;

public class SubscriptionCreatedHandler(
    IEmailService emailService,
    ISellerRepository sellerRepository,
    ITenantRepository tenantRepository,
    ISubscriptionRepository subscriptionRepository,
    ILogger<SubscriptionCreatedHandler> logger)
    : IDomainEventHandler<SubscriptionCreatedEvent>
{
    public async Task HandleAsync(SubscriptionCreatedEvent domainEvent, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Subscription criada: {SubscriptionId} | Tenant: {TenantId} | Seller: {SellerId} | Valor: {Amount}",
            domainEvent.SubscriptionId, domainEvent.TenantId, domainEvent.SellerId, domainEvent.Amount);

        var tenant = await tenantRepository.GetByIdWithConfigAsync(domainEvent.TenantId);
        if (tenant is null) return;

        var seller = await sellerRepository.GetByIdAsync(domainEvent.TenantId, domainEvent.SellerId);
        if (seller is null || string.IsNullOrEmpty(seller.Email)) return;

        var subscription = await subscriptionRepository.GetByIdAsync(domainEvent.TenantId, domainEvent.SubscriptionId);
        if (subscription is null) return;

        var message = new EmailMessage(
            To: seller.Email,
            ToName: seller.LegalName,
            Subject: $"Nova assinatura criada — R$ {domainEvent.Amount:N2}/{domainEvent.Interval}",
            HtmlBody: EmailTemplates.SubscriptionCreated(
                tenant.Name,
                subscription.Description,
                domainEvent.Amount,
                domainEvent.Interval.ToString(),
                subscription.NextBillingDate)
        );

        await emailService.SendAsync(message, cancellationToken);
    }
}
