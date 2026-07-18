namespace FellowCore.Application.Common.Interfaces;

public interface IRealtimeNotifier
{
    Task SendToTenantAsync(Guid tenantId, string eventType, object payload);

    /// <summary>
    /// Push direto pra um seller específico (group "seller-{id}" no SignalR Hub).
    /// Usado pra notificações in-app — só o seller dono vê a invalidação da query.
    /// Sprint 2 Fase 2: <c>NotificationOutboxProcessor</c> chama isso após
    /// materializar a Notification, com eventType="notification.created".
    /// </summary>
    Task SendToSellerAsync(Guid sellerId, string eventType, object payload);
}
