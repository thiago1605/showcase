using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Catálogo PRIVADO de produtos abertos para afiliação. Seller autenticado
/// vê todos produtos PUBLISHED + (AffiliationMode = OPEN | REQUEST) do seu
/// tenant, com filtros opcionais por categoria, ticket, modo.
///
/// Não inclui produtos CLOSED (que não aceitam afiliação) nem do próprio
/// seller — ninguém afilia ao próprio produto. A exclusão acontece no SQL
/// via WHERE OwnerSellerId &lt;&gt; @callerSellerId (índice composto).
///
/// Route:
///   GET /api/v1/marketplace/products  — listagem paginada do catálogo
/// </summary>
[ApiController]
[Route("api/v1/marketplace")]
[Authorize]
[EnableRateLimiting("fixed")]
public class AffiliateMarketplaceController(IProductService productService) : ControllerBase
{
    private (Guid tenantId, Guid sellerId)? RequireSellerScope()
    {
        var info = HttpContext.GetAuthInfo();
        if (info is null || info.IsApiKey || info.SellerId is null) return null;
        return (info.TenantId, info.SellerId.Value);
    }

    [HttpGet("products")]
    public async Task<IActionResult> ListCatalog(
        [FromQuery] string? categories = null,
        [FromQuery] decimal? minPrice = null,
        [FromQuery] decimal? maxPrice = null,
        [FromQuery] AffiliationMode? mode = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;

        // `categories` chega como CSV (?categories=Mentoria,Ebook) — multi-select
        // do chip rail. Mantemos string única no binding pelo limite de bind
        // de arrays em ASP.NET sem [FromQuery(Name=...)] config; parse manual aqui.
        IReadOnlyList<string>? categoryList = null;
        if (!string.IsNullOrWhiteSpace(categories))
        {
            categoryList = categories
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        var result = await productService.ListMarketplaceCatalogAsync(
            tenantId, categoryList, minPrice, maxPrice, mode, page, pageSize,
            excludeOwnerSellerId: sellerId);
        return Ok(result);
    }
}
