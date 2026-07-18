using FellowCore.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace FellowCore.Api.Hubs;

public class SignalRNotifier(IHubContext<NotificationHub> hubContext) : IRealtimeNotifier
{
    public async Task SendToTenantAsync(Guid tenantId, string eventType, object payload)
    {
        await hubContext.Clients.Group($"tenant-{tenantId}").SendAsync("Notification", new
        {
            type = eventType,
            data = payload,
            timestamp = DateTime.UtcNow
        });
    }

    public async Task SendToSellerAsync(Guid sellerId, string eventType, object payload)
    {
        await hubContext.Clients.Group($"seller-{sellerId}").SendAsync("Notification", new
        {
            type = eventType,
            data = payload,
            timestamp = DateTime.UtcNow
        });
    }
}
