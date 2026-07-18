using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Refund queries scoped to a seller. Mutations (create refund) live on
/// `POST /api/v1/transactions/{id}/refund` because they need the parent transaction —
/// this controller only exposes seller-safe lookups for the portal.
///
/// JWT seller-scoped callers: silently filtered to their own SellerId via
/// ResolveSellerScope (extra `?sellerId=` is ignored).
/// API key callers: free to query any seller in their tenant.
/// </summary>
[ApiController]
[Route("api/v1/refunds")]
[AuthOrApiKeyAuth]
[EnableRateLimiting("fixed")]
public class RefundsController(IRefundIntentRepository refundRepository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? sellerId = null,
        [FromQuery] RefundIntentStatus? status = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 200) pageSize = 200;

        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        var tenantId = HttpContext.GetTenantId();

        var (items, total) = await refundRepository.ListAsync(
            tenantId, scopedSellerId, status, from, to, page, pageSize);

        var totalPages = pageSize > 0 ? (int)Math.Ceiling(total / (double)pageSize) : 0;
        return Ok(new
        {
            items = items.Select(r => new
            {
                r.Id,
                r.TenantId,
                r.TransactionId,
                sellerId = r.Transaction?.SellerId,
                amount = r.Amount,
                reason = r.Reason,
                status = r.Status.ToString(),
                providerRefundId = r.ProviderRefundId,
                attemptCount = r.AttemptCount,
                lastError = r.LastError,
                createdAt = r.CreatedAt,
                updatedAt = r.UpdatedAt,
            }),
            totalCount = total,
            page,
            pageSize,
            totalPages,
            hasNext = page < totalPages,
            hasPrevious = page > 1,
        });
    }
}
