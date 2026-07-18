using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Endpoints do produtor pra configurar order bumps (ofertas adicionais no
/// checkout). Sempre seller-scoped — só o owner do mainProduct pode editar.
///
/// Routes:
///   GET    /api/v1/products/{productId}/order-bumps          — listar bumps configurados
///   POST   /api/v1/products/{productId}/order-bumps          — criar
///   PUT    /api/v1/products/{productId}/order-bumps/{id}     — editar
///   DELETE /api/v1/products/{productId}/order-bumps/{id}     — remover
/// </summary>
[ApiController]
[Route("api/v1/products/{productId:guid}/order-bumps")]
[Authorize]
[EnableRateLimiting("fixed")]
public class ProductOrderBumpsController(IProductOrderBumpService service) : ControllerBase
{
    private (Guid tenantId, Guid sellerId)? RequireSellerScope()
    {
        var info = HttpContext.GetAuthInfo();
        if (info is null || info.IsApiKey || info.SellerId is null) return null;
        return (info.TenantId, info.SellerId.Value);
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid productId)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var bumps = await service.ListAsync(tenantId, sellerId, productId);
        return Ok(bumps);
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid productId, [FromBody] CreateOrderBumpDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var bump = await service.CreateAsync(tenantId, sellerId, productId, request);
        return Ok(bump);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid productId, Guid id, [FromBody] UpdateOrderBumpDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var bump = await service.UpdateAsync(tenantId, sellerId, productId, id, request);
        return Ok(bump);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid productId, Guid id)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        await service.DeleteAsync(tenantId, sellerId, productId, id);
        return NoContent();
    }
}
