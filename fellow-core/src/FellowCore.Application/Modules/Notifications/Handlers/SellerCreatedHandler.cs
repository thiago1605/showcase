using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Email.Templates;
using FellowCore.Domain.Events;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Notifications.Handlers;

public class SellerCreatedHandler(
    IEmailService emailService,
    ISellerRepository sellerRepository,
    ITenantRepository tenantRepository,
    ILogger<SellerCreatedHandler> logger)
    : IDomainEventHandler<SellerCreatedEvent>
{
    public async Task HandleAsync(SellerCreatedEvent domainEvent, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Seller criado: {SellerId} | Tenant: {TenantId} | Documento: {Document} | Email: {Email}",
            domainEvent.SellerId, domainEvent.TenantId, domainEvent.Document, domainEvent.Email);

        if (string.IsNullOrWhiteSpace(domainEvent.Email))
            return;

        var seller = await sellerRepository.GetByIdAsync(domainEvent.TenantId, domainEvent.SellerId);
        var sellerName = seller?.LegalName ?? domainEvent.Email;

        var tenant = await tenantRepository.GetByIdWithConfigAsync(domainEvent.TenantId);
        var tenantName = tenant?.Name ?? "Fellow Pay";

        var message = new EmailMessage(
            To: domainEvent.Email,
            ToName: sellerName,
            Subject: $"Sua conta foi criada em {tenantName}",
            HtmlBody: EmailTemplates.SellerWelcome(sellerName, domainEvent.Email, tenantName)
        );

        await emailService.SendAsync(message, cancellationToken);
    }
}
