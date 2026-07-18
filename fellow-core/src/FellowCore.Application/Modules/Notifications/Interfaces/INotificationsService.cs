using FellowCore.Application.Modules.Notifications.DTOs;

namespace FellowCore.Application.Modules.Notifications.Interfaces;

public interface INotificationsService
{
    void NotifyTransactionUpdate(NotificationJobData data);
}

public interface ITenantWebhookProcessor
{
    Task ProcessAsync(NotificationJobData job);
}

