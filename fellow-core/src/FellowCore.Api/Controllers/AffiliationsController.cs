using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Endpoints de afiliações. Mistura dois lados:
///  - Afiliado (seller logado pedindo/listando suas próprias afiliações)
///  - Produtor (gerencia approve/reject/revoke das afiliações dos seus produtos)
///
/// Authz é feito no Service — verifica owner do produto pra approve/reject/revoke.
///
/// Routes:
///   POST  /api/v1/affiliations                  — afiliado solicita
///   GET   /api/v1/affiliations                  — afiliado lista as suas
///   GET   /api/v1/affiliations/{id}             — detalhe (afiliado ou produtor)
///   POST  /api/v1/affiliations/{id}/approve     — produtor aprova
///   POST  /api/v1/affiliations/{id}/reject      — produtor rejeita
///   POST  /api/v1/affiliations/{id}/revoke      — produtor revoga
/// </summary>
[ApiController]
[Route("api/v1/affiliations")]
[Authorize]
[EnableRateLimiting("fixed")]
public class AffiliationsController(IAffiliationService affiliationService) : ControllerBase
{
    private (Guid tenantId, Guid sellerId, Guid? userId)? RequireSellerScope()
    {
        var info = HttpContext.GetAuthInfo();
        if (info is null || info.IsApiKey || info.SellerId is null) return null;
        return (info.TenantId, info.SellerId.Value, info.UserId);
    }

    [HttpPost]
    public async Task<IActionResult> RequestAffiliation([FromBody] RequestAffiliationDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId, _) = scope.Value;
        var aff = await affiliationService.RequestAsync(tenantId, sellerId, request);
        return CreatedAtAction(nameof(GetById), new { id = aff.Id }, aff);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] AffiliationStatus? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId, _) = scope.Value;
        var result = await affiliationService.ListBySellerAsync(tenantId, sellerId, status, page, pageSize);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId, _) = scope.Value;
        var aff = await affiliationService.GetByIdAsync(tenantId, sellerId, id);
        if (aff is null) return NotFound();
        return Ok(aff);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApproveAffiliationDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId, userId) = scope.Value;
        return Ok(await affiliationService.ApproveAsync(tenantId, sellerId, userId, id, request));
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectAffiliationDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId, userId) = scope.Value;
        return Ok(await affiliationService.RejectAsync(tenantId, sellerId, userId, id, request));
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, [FromBody] RevokeAffiliationDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId, userId) = scope.Value;
        return Ok(await affiliationService.RevokeAsync(tenantId, sellerId, userId, id, request));
    }

    /// <summary>
    /// Mini-stats de TODAS as afiliações do seller — usado pra enriquecer a
    /// lista de /affiliations no front com sales/clicks/earnings 30d inline.
    /// Mais barato que chamar N × /stats.
    /// </summary>
    [HttpGet("me/mini-stats")]
    public async Task<IActionResult> GetMyMiniStats()
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId, _) = scope.Value;
        var stats = await affiliationService.GetMyMiniStatsAsync(tenantId, sellerId);
        return Ok(stats);
    }

    /// <summary>
    /// Métricas de performance da afiliação (TPV, ganhos, vendas) em 2 janelas
    /// (30d / all-time) + saldo pendente. Acessível pelo afiliado dono OU pelo
    /// produtor do produto. Outros sellers do tenant recebem 403.
    /// </summary>
    [HttpGet("{id:guid}/stats")]
    public async Task<IActionResult> GetStats(Guid id, [FromQuery] int days = 30)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId, _) = scope.Value;
        var stats = await affiliationService.GetStatsAsync(tenantId, sellerId, id, days);
        if (stats is null) return NotFound();
        return Ok(stats);
    }
}
