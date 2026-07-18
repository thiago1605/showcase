using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Events;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Notifications.Handlers;

public class TransactionCreatedHandler(
    IRealtimeNotifier realtimeNotifier,
    ILogger<TransactionCreatedHandler> logger)
    : IDomainEventHandler<TransactionCreatedEvent>
{
    public async Task HandleAsync(TransactionCreatedEvent domainEvent, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Transação criada: {TransactionId} | Tenant: {TenantId} | Valor: {Amount} | Tipo: {PaymentType} | Provedor: {Provider}",
            domainEvent.TransactionId, domainEvent.TenantId, domainEvent.Amount,
            domainEvent.PaymentType, domainEvent.Provider);

        await realtimeNotifier.SendToTenantAsync(domainEvent.TenantId, "transaction.created", new
        {
            transactionId = domainEvent.TransactionId,
            amount = domainEvent.Amount,
            paymentType = domainEvent.PaymentType.ToString(),
            provider = domainEvent.Provider.ToString()
        });
    }
}
