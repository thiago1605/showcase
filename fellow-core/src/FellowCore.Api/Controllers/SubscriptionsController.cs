using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Modules.Subscriptions.DTOs;
using FellowCore.Application.Modules.Subscriptions.Interfaces;
using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/subscriptions")]
[AuthOrApiKeyAuth]
[EnableRateLimiting("fixed")]
public class SubscriptionsController(ISubscriptionService subscriptionService) : ControllerBase
{
    [HttpPost]
    [AuditAction("subscription.created")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateSubscriptionDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();

        var info = HttpContext.GetAuthInfo();
        if (info is { IsJwt: true, SellerId: { } scopedSellerId })
        {
            if (request.SellerId != Guid.Empty && request.SellerId != scopedSellerId)
                return StatusCode(StatusCodes.Status403Forbidden);
            request = request with { SellerId = scopedSellerId };
        }

        var result = await subscriptionService.CreateAsync(tenantId, request);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await subscriptionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr404(result.SellerId) is { } block) return block;
        return Ok(result);
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? sellerId = null,
        [FromQuery] SubscriptionStatus? status = null)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();
        var filter = new SubscriptionFilterDto(page, pageSize, scopedSellerId, status);
        var result = await subscriptionService.ListAsync(tenantId, filter);
        return Ok(result);
    }

    [HttpPost("{id:guid}/cancel")]
    [AuditAction("subscription.canceled")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await subscriptionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr403(existing.SellerId) is { } block) return block;

        var result = await subscriptionService.CancelAsync(tenantId, id);
        return Ok(result);
    }

    [HttpPost("{id:guid}/pause")]
    [AuditAction("subscription.paused")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Pause(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await subscriptionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr403(existing.SellerId) is { } block) return block;

        var result = await subscriptionService.PauseAsync(tenantId, id);
        return Ok(result);
    }

    [HttpPost("{id:guid}/resume")]
    [AuditAction("subscription.resumed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resume(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await subscriptionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr403(existing.SellerId) is { } block) return block;

        var result = await subscriptionService.ResumeAsync(tenantId, id);
        return Ok(result);
    }
}
