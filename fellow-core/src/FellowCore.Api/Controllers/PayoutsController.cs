using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Payouts.DTOs;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/payouts")]
[AuthOrApiKeyAuth]
[EnableRateLimiting("fixed")]
public class PayoutsController(
    IPayoutService payoutService,
    IWithdrawService withdrawService,
    IExportService exportService) : ControllerBase
{
    [HttpPost]
    [AuditAction("payout.created")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreatePayoutDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();

        // JWT seller-scoped: forçar SellerId ao próprio (ignorando body) e bloquear se body
        // tentar payout para outro seller.
        var info = HttpContext.GetAuthInfo();
        if (info is { IsJwt: true, SellerId: { } scopedSellerId })
        {
            if (request.SellerId != Guid.Empty && request.SellerId != scopedSellerId)
                return StatusCode(StatusCodes.Status403Forbidden);
            request = request with { SellerId = scopedSellerId };
        }

        var result = await payoutService.CreateAsync(tenantId, request);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    /// <summary>
    /// Saque comercial (D+0 com taxa 1% ou D+1 grátis). Aplica todas as regras
    /// da spec 2026: min R$ 50, max per-seller, cap diário Woovi R$ 48.800,
    /// tarifa fixa R$ 1 quando &lt; R$ 500. Excedendo o cap, agenda pra D+1
    /// automaticamente (fila FIFO).
    /// </summary>
    [HttpPost("withdraw")]
    [AuditAction("withdraw.requested")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Withdraw([FromBody] CreateWithdrawDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();

        var info = HttpContext.GetAuthInfo();
        Guid effectiveSellerId = request.SellerId;
        if (info is { IsJwt: true, SellerId: { } scopedSellerId })
        {
            if (effectiveSellerId != Guid.Empty && effectiveSellerId != scopedSellerId)
                return StatusCode(StatusCodes.Status403Forbidden);
            effectiveSellerId = scopedSellerId;
        }

        var result = await withdrawService.RequestAsync(
            tenantId,
            effectiveSellerId,
            request.Amount,
            request.Type ?? Domain.Enums.WithdrawType.D1);

        // 202 Accepted reflete que o saque foi aceito — execução pode ser síncrona
        // (D+0) ou assíncrona (D+1/agendado). Cliente confere status via GET /payouts/{id}.
        return Accepted(value: result);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await payoutService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr404(result.SellerId) is { } block) return block;
        return Ok(result);
    }

    [HttpGet("export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Export(
        [FromQuery] string format = "csv",
        [FromQuery] Guid? sellerId = null,
        [FromQuery] PayoutStatus? status = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();
        var fmt = format.ToLowerInvariant();
        var fileName = $"payouts_{DateTime.UtcNow:yyyyMMdd}";

        if (fmt == "pdf")
        {
            var pdf = await exportService.ExportPayoutsPdfAsync(tenantId, from, to, scopedSellerId, status);
            return File(pdf, "application/pdf", $"{fileName}.pdf");
        }

        var csv = await exportService.ExportPayoutsCsvAsync(tenantId, from, to, scopedSellerId, status);
        return File(csv, "text/csv", $"{fileName}.csv");
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? sellerId = null,
        [FromQuery] PayoutStatus? status = null)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();
        var filter = new PayoutFilterDto(page, pageSize, scopedSellerId, status);
        var result = await payoutService.ListAsync(tenantId, filter);
        return Ok(result);
    }
}
