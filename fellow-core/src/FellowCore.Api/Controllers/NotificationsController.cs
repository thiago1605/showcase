using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Notifications.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Notificações in-app do seller logado. Sempre scoped pelo JWT (seller_id
/// claim) — operadores da plataforma (sem seller_id) recebem 403.
///
/// Frontend chama (Fase 1):
///  - GET /api/v1/notifications?page=1&pageSize=20  — listagem paginada
///  - GET /api/v1/notifications/unread-count        — badge no header (polling)
///  - POST /api/v1/notifications/{id}/read          — marcar uma como lida
///  - POST /api/v1/notifications/read-all           — bulk
///
/// Polling do frontend acontece a cada 30s pro unread-count. Lista é refetch
/// on dropdown open (não tem polling agressivo na lista cheia — só no count).
/// </summary>
[ApiController]
[Route("api/v1/notifications")]
[Authorize]
[EnableRateLimiting("fixed")]
public class NotificationsController(INotificationService notificationService) : ControllerBase
{
    private (Guid tenantId, Guid sellerId)? RequireSellerScope()
    {
        var info = HttpContext.GetAuthInfo();
        if (info is null || info.IsApiKey || info.SellerId is null)
            return null;
        return (info.TenantId, info.SellerId.Value);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool unreadOnly = false)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);

        var (tenantId, sellerId) = scope.Value;
        var result = await notificationService.ListAsync(
            tenantId, sellerId, page, pageSize, unreadOnly);
        return Ok(result);
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCount()
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);

        var (tenantId, sellerId) = scope.Value;
        var count = await notificationService.GetUnreadCountAsync(tenantId, sellerId);
        return Ok(new { count });
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);

        var (tenantId, sellerId) = scope.Value;
        var ok = await notificationService.MarkReadAsync(tenantId, sellerId, id);
        if (!ok) return NotFound();
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);

        var (tenantId, sellerId) = scope.Value;
        var affected = await notificationService.MarkAllReadAsync(tenantId, sellerId);
        return Ok(new { affected });
    }
}
