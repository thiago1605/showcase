using FellowCore.Application.Modules.Notifications.DTOs;

namespace FellowCore.Application.Modules.Notifications.Interfaces;

public interface INotificationsProcessor
{
    Task ProcessAsync(NotificationJobData jobData);
}