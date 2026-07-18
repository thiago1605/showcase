using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Endpoints de cupons. Producer-only (cria/lista/deleta). Validação pública
/// (= aplicar no checkout) vive em PublicCheckoutController pra reusar contexto
/// de slug.
///
/// Routes:
///   POST   /api/v1/coupons                — cria
///   GET    /api/v1/coupons                — lista (filter ?productId={id} pra restringir)
///   DELETE /api/v1/coupons/{id}           — remove
/// </summary>
[ApiController]
[Route("api/v1/coupons")]
[Authorize]
[EnableRateLimiting("fixed")]
public class CouponsController(ICouponService couponService) : ControllerBase
{
    private (Guid tenantId, Guid sellerId)? RequireSellerScope()
    {
        var info = HttpContext.GetAuthInfo();
        if (info is null || info.IsApiKey || info.SellerId is null) return null;
        return (info.TenantId, info.SellerId.Value);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCouponDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var coupon = await couponService.CreateAsync(tenantId, sellerId, request);
        return Ok(coupon);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? productId = null)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var coupons = await couponService.ListAsync(tenantId, sellerId, productId);
        return Ok(coupons);
    }

    /// <summary>
    /// Lista unificada pro painel /coupons do produtor: cupons globais do
    /// tenant + cupons específicos dos produtos que o seller é OwnerSeller.
    /// Cada item inclui ProductName quando aplicável.
    /// </summary>
    [HttpGet("mine")]
    public async Task<IActionResult> ListMine()
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var coupons = await couponService.ListByOwnerAsync(tenantId, sellerId);
        return Ok(coupons);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        await couponService.DeleteAsync(tenantId, sellerId, id);
        return NoContent();
    }
}
