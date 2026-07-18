using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Receipts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/receipts")]
[AuthOrApiKeyAuth]
[EnableRateLimiting("fixed")]
public class ReceiptsController(IReceiptService receiptService) : ControllerBase
{
    /// <summary>
    /// Generation endpoints (POST) are administrative — they should be triggered by the
    /// platform's own workflows (after a payment captures, after a payout settles, etc.)
    /// or by B2B integrations using an API key. A seller-scoped JWT is never expected
    /// here; the seller portal only consumes already-generated receipts.
    /// </summary>
    private IActionResult? RejectIfSellerScoped()
    {
        var info = HttpContext.GetAuthInfo();
        if (info is { IsJwt: true, SellerId: not null })
            return StatusCode(StatusCodes.Status403Forbidden);
        return null;
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var tenantId = HttpContext.GetTenantId();
        var receipt = await receiptService.GetByIdAsync(tenantId, id);
        if (receipt is null) return NotFound();
        if (this.EnforceOwnershipOr404(receipt.SellerId) is { } block) return block;

        return Ok(new
        {
            receipt.Id,
            receipt.TenantId,
            receipt.SellerId,
            receipt.TransactionId,
            receipt.PayoutId,
            receipt.RefundIntentId,
            Type = receipt.Type.ToString(),
            Provider = receipt.Provider.ToString(),
            receipt.ProviderReceiptId,
            Status = receipt.Status.ToString(),
            receipt.Amount,
            receipt.Currency,
            receipt.PdfStorageKey,
            receipt.PublicUrl,
            receipt.Description,
            receipt.CreatedAt
        });
    }

    [HttpGet("seller/{sellerId:guid}")]
    public async Task<IActionResult> GetBySeller(Guid sellerId, [FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        var tenantId = HttpContext.GetTenantId();

        // sellerId está no PATH — intenção explícita. Para JWT seller-scoped, exigir
        // que coincida com o próprio seller. Caso contrário, 403 (em vez do "ignorar"
        // que aplicamos em filtros opcionais de query).
        var info = HttpContext.GetAuthInfo();
        if (info is { IsJwt: true, SellerId: { } scopedSellerId } && scopedSellerId != sellerId)
            return StatusCode(StatusCodes.Status403Forbidden);

        var receipts = await receiptService.GetBySellerAsync(tenantId, sellerId, limit, offset);

        return Ok(receipts.Select(r => new
        {
            r.Id,
            r.SellerId,
            r.TransactionId,
            r.PayoutId,
            Type = r.Type.ToString(),
            Status = r.Status.ToString(),
            r.Amount,
            r.Description,
            r.CreatedAt
        }));
    }

    [HttpPost("transaction/{transactionId:guid}")]
    public async Task<IActionResult> GenerateForTransaction(Guid transactionId)
    {
        if (RejectIfSellerScoped() is { } block) return block;
        var tenantId = HttpContext.GetTenantId();
        var receipt = await receiptService.GenerateForPaymentAsync(tenantId, transactionId);
        return Ok(new { receipt.Id, Type = receipt.Type.ToString(), receipt.Amount, receipt.CreatedAt });
    }

    [HttpPost("payout/{payoutId:guid}")]
    public async Task<IActionResult> GenerateForPayout(Guid payoutId)
    {
        if (RejectIfSellerScoped() is { } block) return block;
        var tenantId = HttpContext.GetTenantId();
        var receipt = await receiptService.GenerateForPayoutAsync(tenantId, payoutId);
        return Ok(new { receipt.Id, Type = receipt.Type.ToString(), receipt.Amount, receipt.CreatedAt });
    }

    [HttpPost("refund/{refundIntentId:guid}")]
    public async Task<IActionResult> GenerateForRefund(Guid refundIntentId)
    {
        if (RejectIfSellerScoped() is { } block) return block;
        var tenantId = HttpContext.GetTenantId();
        var receipt = await receiptService.GenerateForRefundAsync(tenantId, refundIntentId);
        return Ok(new { receipt.Id, Type = receipt.Type.ToString(), receipt.Amount, receipt.CreatedAt });
    }
}
