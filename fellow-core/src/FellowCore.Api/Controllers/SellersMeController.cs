using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Sellers.DTOs;
using FellowCore.Application.Modules.Sellers.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Seller portal endpoints, JWT-only. The seller is always inferred from the
/// authenticated principal (claim seller_id) — never from a query/route parameter.
/// Platform operators (no seller_id in their JWT) cannot use these endpoints; they
/// must use the admin SellersController with the explicit /{id} routes.
///
/// Pairs with `fellow-pay/src/services/dashboard.service.ts → getBalance()` and the
/// upcoming portal calls for own-profile / own-statement.
/// </summary>
[ApiController]
[Route("api/v1/sellers/me")]
[Authorize]
[EnableRateLimiting("fixed")]
public class SellersMeController(ISellerService sellerService, ISellerTierService sellerTierService) : ControllerBase
{
    private (Guid tenantId, Guid sellerId)? RequireSellerScope()
    {
        var info = HttpContext.GetAuthInfo();
        if (info is null || info.IsApiKey || info.SellerId is null)
            return null;
        return (info.TenantId, info.SellerId.Value);
    }

    [HttpGet]
    public async Task<IActionResult> GetProfile()
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);

        var (tenantId, sellerId) = scope.Value;
        var seller = await sellerService.GetByIdAsync(tenantId, sellerId);
        return Ok(seller);
    }

    /// <summary>
    /// Atualiza os dados do próprio seller (portal). Equivalente ao PATCH /sellers/{id} do
    /// admin, mas sem precisar passar o id na URL — derivado sempre do JWT.
    /// </summary>
    [HttpPatch]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateSellerDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);

        var (tenantId, sellerId) = scope.Value;
        var updated = await sellerService.UpdateAsync(tenantId, sellerId, request);
        return Ok(updated);
    }

    [HttpGet("balance")]
    public async Task<IActionResult> GetBalance()
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);

        var (tenantId, sellerId) = scope.Value;
        var balance = await sellerService.GetBalanceAsync(tenantId, sellerId);
        return Ok(balance);
    }

    /// <summary>
    /// Sprint 0: retorna o tier de performance do seller logado, calculado on-the-fly
    /// a partir do TPV30d. Não persiste — Sprint 1 substitui por job mensal + cooldown.
    /// Inclui também o status Founding (ortogonal a tier).
    ///
    /// Resposta consumida pelo portal pra:
    ///   - mostrar tier atual ("Você é Gold")
    ///   - barra de progresso até o próximo tier ("R$ 150k pra Diamond")
    ///   - badge Founding ("Founding Seller #007")
    /// </summary>
    [HttpGet("tier")]
    public async Task<IActionResult> GetTier()
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);

        var (tenantId, sellerId) = scope.Value;
        var tier = await sellerTierService.GetTierAsync(tenantId, sellerId);
        return Ok(tier);
    }

    [HttpGet("statement")]
    public async Task<IActionResult> GetStatement([FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);

        var (tenantId, sellerId) = scope.Value;
        // Default window: últimos 30 dias se nada for passado. O service exige start/end
        // não-nulos via FluentValidation; mantemos a flexibilidade do contrato e damos
        // um default seguro pro consumidor do portal.
        var rangeEnd = end ?? DateTime.UtcNow;
        var rangeStart = start ?? rangeEnd.AddDays(-30);
        var entries = await sellerService.GetStatementAsync(tenantId, sellerId, rangeStart, rangeEnd);
        return Ok(entries);
    }
}
