using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FellowCore.Api.Hubs;

/// <summary>
/// SignalR Hub pra push real-time. Connection é authorize-only (JWT bearer).
/// Cada client entra em dois groups com base nas claims do JWT:
///  - <c>tenant-{id}</c>: eventos tenant-wide (payout.completed em outro fluxo legado).
///  - <c>seller-{id}</c>: eventos seller-scoped (notification.created — Sprint 2 Fase 2).
///
/// Sellers só recebem seu próprio seller group; operadores da plataforma (sem
/// seller_id claim) só recebem tenant events. Defensive — não vaza notification
/// de seller A pra seller B do mesmo tenant.
/// </summary>
[Authorize]
public class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrEmpty(tenantId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant-{tenantId}");

        var sellerId = Context.User?.FindFirst("seller_id")?.Value;
        if (!string.IsNullOrEmpty(sellerId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"seller-{sellerId}");

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var tenantId = Context.User?.FindFirst("tenant_id")?.Value;
        if (!string.IsNullOrEmpty(tenantId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"tenant-{tenantId}");

        var sellerId = Context.User?.FindFirst("seller_id")?.Value;
        if (!string.IsNullOrEmpty(sellerId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"seller-{sellerId}");

        await base.OnDisconnectedAsync(exception);
    }
}
