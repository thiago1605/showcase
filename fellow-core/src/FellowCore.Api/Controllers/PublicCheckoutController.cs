using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Endpoints públicos do checkout de produto (modelo Kirvano). SEM AUTH —
/// qualquer um pode resolver um produto publicado pelo slug e iniciar o
/// fluxo de pagamento. Atribuição de afiliado é feita via query param
/// <c>?aff={trackingCode}</c>.
///
/// Tenant é inferido do produto (Products.TenantId) — não vem do request.
/// Por isso é seguro deixar anônimo: o caller não pode escolher o tenant
/// nem o seller (produtor) — eles vêm do produto resolvido.
///
/// Rate limit fixo aplicado pra mitigar enumeration/scraping. Não é proteção
/// total — pra apps abertos depende de WAF/Cloudflare upstream.
///
/// Routes:
///   GET  /api/v1/public/products/{slug}            — info pública do produto (+ aff opcional)
///   POST /api/v1/public/products/{slug}/checkout   — cria TX com auto-split
///   GET  /api/v1/public/transactions/{id}/status   — polling público do status da TX
/// </summary>
[ApiController]
[Route("api/v1/public")]
[AllowAnonymous]
[EnableRateLimiting("fixed")]
public class PublicCheckoutController(
    IMarketplaceCheckoutService checkoutService,
    ICouponService couponService,
    IProductOrderBumpService orderBumpService) : ControllerBase
{
    [HttpGet("products/{slug}")]
    public async Task<IActionResult> ResolveProduct(string slug, [FromQuery] string? aff = null)
    {
        var product = await checkoutService.ResolveAsync(slug, aff);
        if (product is null) return NotFound();
        return Ok(product);
    }

    [HttpPost("products/{slug}/checkout")]
    public async Task<IActionResult> Checkout(string slug, [FromBody] PublicCheckoutRequestDto request)
    {
        var tx = await checkoutService.CheckoutAsync(slug, request);
        return Ok(tx);
    }

    /// <summary>
    /// Polling de status da TX após o checkout — pra cartão (aguarda webhook
    /// pós-confirmação Elements) e PIX (aguarda webhook do banco). Retorna 404
    /// se a TX não bate com o produto do slug — evita enumeration de TX de
    /// outros produtos.
    /// </summary>
    [HttpGet("products/{slug}/transactions/{transactionId:guid}/status")]
    public async Task<IActionResult> GetTransactionStatus(string slug, Guid transactionId)
    {
        var status = await checkoutService.GetStatusAsync(slug, transactionId);
        if (status is null) return NotFound();
        return Ok(status);
    }

    /// <summary>
    /// Lista order bumps ativos do produto referenciado pelo slug. Anônimo —
    /// retorna só bumps cujo bumpProduct está PUBLISHED. Lista vazia se nenhum
    /// bump foi configurado. 404 se o slug não resolve em produto publicado
    /// (mesmo comportamento do ResolveProduct).
    /// </summary>
    [HttpGet("products/{slug}/order-bumps")]
    public async Task<IActionResult> ListOrderBumps(string slug)
    {
        var bumps = await orderBumpService.ListPublicForSlugAsync(slug);
        if (bumps is null) return NotFound();
        return Ok(bumps);
    }

    /// <summary>
    /// Valida um cupom pra um produto. Retorna 404 se o cupom não existe,
    /// está expirado, esgotado, ou não se aplica ao produto. Em sucesso,
    /// devolve o valor de desconto calculado pro preço atual.
    /// </summary>
    [HttpGet("products/{slug}/coupons/{code}/check")]
    public async Task<IActionResult> CheckCoupon(string slug, string code)
    {
        var result = await couponService.ValidateAsync(slug, code);
        if (result is null) return NotFound();
        return Ok(result);
    }

    /// <summary>
    /// Registra um click no link de divulgação de afiliação. Fire-and-forget
    /// pelo frontend: chamado ao montar /p/[slug] quando ?aff= bate numa
    /// afiliação válida. Dedup interno por fingerprint+afiliação em janela 1h.
    /// Retorna 204 mesmo se a afiliação for inválida — não-existência do
    /// trackingCode não deve ser observável por um endpoint público (info leak).
    /// </summary>
    [HttpPost("affiliates/{trackingCode}/click")]
    public async Task<IActionResult> RegisterClick(string trackingCode)
    {
        // IP atrás de Cloudflare Tunnel: prioriza CF-Connecting-IP (header
        // injetado pela Cloudflare com o IP cliente original). Fallback:
        // X-Forwarded-For (1º elemento) → RemoteIpAddress (conexão TCP direta).
        var ip = Request.Headers["CF-Connecting-IP"].FirstOrDefault()
              ?? Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim()
              ?? HttpContext.Connection.RemoteIpAddress?.ToString()
              ?? "unknown";
        var ua = Request.Headers["User-Agent"].FirstOrDefault() ?? "unknown";
        var referrer = Request.Headers["Referer"].FirstOrDefault();

        await checkoutService.RecordAffiliateClickAsync(trackingCode, ip, ua, referrer);
        return NoContent();
    }
}
