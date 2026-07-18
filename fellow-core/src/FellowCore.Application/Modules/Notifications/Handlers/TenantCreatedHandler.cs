using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Email.Templates;
using FellowCore.Domain.Events;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Notifications.Handlers;

public class TenantCreatedHandler(
    IEmailService emailService,
    ITenantRepository tenantRepository,
    ILogger<TenantCreatedHandler> logger)
    : IDomainEventHandler<TenantCreatedEvent>
{
    public async Task HandleAsync(TenantCreatedEvent domainEvent, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Tenant criado: {TenantId} | Nome: {Name} | Slug: {Slug}",
            domainEvent.TenantId, domainEvent.Name, domainEvent.Slug);

        if (string.IsNullOrWhiteSpace(domainEvent.OwnerEmail))
            return;

        var tenant = await tenantRepository.GetByIdWithConfigAsync(domainEvent.TenantId);
        if (tenant is null) return;

        var message = new EmailMessage(
            To: domainEvent.OwnerEmail,
            ToName: tenant.Name,
            Subject: $"Bem-vindo à Fellow Pay, {tenant.Name}!",
            HtmlBody: EmailTemplates.TenantWelcome(tenant.Name, domainEvent.OwnerEmail, tenant.ApiKeyPrefix)
        );

        await emailService.SendAsync(message, cancellationToken);
    }
}
