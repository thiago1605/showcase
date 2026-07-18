using FellowCore.Application.Modules.Notifications.DTOs;
using FellowCore.Application.Modules.Notifications.Interfaces;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Notifications;

public class NotificationsService(
    IBackgroundJobClient backgroundJobClient, 
    ILogger<NotificationsService> logger) : INotificationsService
{
    public void NotifyTransactionUpdate(NotificationJobData data)
    {
        logger.LogInformation("Enfileirando notificacoes para Tenant {TenantId} / Seller {SellerId}", data.TenantId, data.SellerId);

        backgroundJobClient.Enqueue<INotificationsProcessor>(processor => processor.ProcessAsync(data));
        backgroundJobClient.Enqueue<ITenantWebhookProcessor>(processor => processor.ProcessAsync(data));
    }
}