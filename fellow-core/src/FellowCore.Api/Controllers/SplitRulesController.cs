using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Modules.Splits.DTOs;
using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Application.Modules.Splits.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

// Split rules are now scoped by SplitRule.OwnerSellerId (nullable):
//   - owner == null  → legacy/tenant-wide rule, manageable only via API key.
//   - owner == Sx    → rule belongs to seller Sx; only Sx (JWT) or API key can edit/delete.
// Visibility for a JWT seller is owner-or-recipient: a seller sees rules they created
// AND rules where they appear as a recipient (so they can audit how shares affect them).
[ApiController]
[Route("api/v1/split-rules")]
[EnableRateLimiting("fixed")]
public class SplitRulesController(ISplitRuleService splitRuleService, ISplitSimulatorService splitSimulatorService) : ControllerBase
{
    [HttpPost]
    [AuthOrApiKeyAuth]
    [AuditAction("split-rule.created")]
    public async Task<IActionResult> Create([FromBody] CreateSplitRuleDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();

        // For JWT sellers: ownership is forced to the caller's SellerId. Body cannot
        // override this — that would let a seller create rules in another seller's name.
        var info = HttpContext.GetAuthInfo();
        Guid? ownerSellerId = null;
        if (info is { IsJwt: true, SellerId: { } scopedSellerId })
            ownerSellerId = scopedSellerId;

        var result = await splitRuleService.CreateAsync(tenantId, request, ownerSellerId);

        return CreatedAtAction(
            actionName: nameof(GetById),
            routeValues: new { id = result.Id },
            value: result
        );
    }

    [HttpGet]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var info = HttpContext.GetAuthInfo();
        // JWT seller-scoped → only rules owned by them OR where they're a recipient.
        // ApiKey/operator → all tenant rules.
        var sellerScope = info is { IsJwt: true, SellerId: { } sid } ? (Guid?)sid : null;

        var result = await splitRuleService.ListAsync(tenantId, page, pageSize, sellerScope);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> GetById(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await splitRuleService.GetByIdAsync(tenantId, id);

        // Seller can only see rules they own OR participate as recipient. Anything else
        // is a 404 (don't leak existence).
        var info = HttpContext.GetAuthInfo();
        if (info is { IsJwt: true, SellerId: { } sid })
        {
            var isOwner = result.OwnerSellerId == sid;
            var isRecipient = result.Recipients.Any(r => r.SellerId == sid);
            if (!isOwner && !isRecipient)
                return NotFound();
        }

        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [AuthOrApiKeyAuth]
    [AuditAction("split-rule.deactivated")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var info = HttpContext.GetAuthInfo();

        // Seller JWT: can only deactivate rules they own. Need to load the rule first to
        // check ownership — 404 if it doesn't exist for the tenant, 403 if it exists but
        // isn't theirs (this latter signal is intentional: the seller may know about the
        // rule via being a recipient and we want to be explicit they can't act on it).
        if (info is { IsJwt: true, SellerId: { } sid })
        {
            var existing = await splitRuleService.GetByIdAsync(tenantId, id);
            if (existing.OwnerSellerId != sid)
                return StatusCode(StatusCodes.Status403Forbidden);
        }

        await splitRuleService.DeactivateAsync(tenantId, id);
        return NoContent();
    }

    [HttpPost("simulate")]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> Simulate([FromBody] SimulateSplitRequest request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await splitSimulatorService.SimulateAsync(tenantId, request);
        return Ok(result);
    }

    [HttpPost("{id:guid}/activate")]
    [AuthOrApiKeyAuth]
    [AuditAction("split-rule.activated")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Activate(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var info = HttpContext.GetAuthInfo();

        // JWT seller: only activate rules they own. Pre-load to enforce ownership before
        // touching state; a seller-as-recipient must NOT be able to flip the active flag
        // on someone else's rule.
        if (info is { IsJwt: true, SellerId: { } sid })
        {
            var existing = await splitRuleService.GetByIdAsync(tenantId, id);
            if (existing.OwnerSellerId != sid)
                return StatusCode(StatusCodes.Status403Forbidden);
        }

        await splitRuleService.ActivateAsync(tenantId, id);
        return NoContent();
    }
}
