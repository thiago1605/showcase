using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Dashboard.DTOs;
using FellowCore.Application.Modules.Dashboard.Services;
using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/dashboard")]
[EnableRateLimiting("fixed")]
public class DashboardController(IDashboardService dashboardService) : ControllerBase
{
    /// <summary>
    /// Aggregated dashboard summary for the authenticated tenant.
    /// Accepts both JWT (seller portal) and X-Api-Key (B2B). When the caller is a
    /// JWT user with seller_id, the response is forced to that seller's scope —
    /// any explicit ?sellerId= in the query is silently ignored, per the
    /// "seller-scoped wins over role" rule. JWT users SEM seller_id e SEM role
    /// de platform operator recebem 403 (Auth.NoSellerScope) — não vazamos
    /// agregado da tenant para users não vinculados a um produtor.
    /// </summary>
    [HttpGet]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? sellerId,
        [FromQuery] PaymentProvider? provider)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();

        var result = await dashboardService.GetSummaryAsync(
            tenantId,
            new DashboardFilterDto(from, to, scopedSellerId, provider));
        return Ok(result);
    }

    /// <summary>
    /// Time-series of transaction volume aggregated por dia ou semana — alimenta
    /// o gráfico de linha da dashboard e os sparklines dos KPI cards. Mesmo escopo
    /// do summary: JWT seller é forçado pro próprio sellerId, ApiKey aceita filtro.
    /// </summary>
    [HttpGet("timeseries")]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> GetTimeseries(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? sellerId,
        [FromQuery] PaymentProvider? provider,
        [FromQuery] DashboardGranularity? granularity)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();

        var result = await dashboardService.GetTimeseriesAsync(
            tenantId,
            new DashboardFilterDto(from, to, scopedSellerId, provider),
            granularity);
        return Ok(result);
    }

    /// <summary>
    /// Top N clientes (agrupados por PayerEmail) por volume no período. Mesmo escopo do summary.
    /// </summary>
    [HttpGet("top-customers")]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> GetTopCustomers(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? sellerId,
        [FromQuery] PaymentProvider? provider,
        [FromQuery] int limit = 5)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();

        var result = await dashboardService.GetTopCustomersAsync(
            tenantId,
            new DashboardFilterDto(from, to, scopedSellerId, provider),
            Math.Clamp(limit, 1, 50));
        return Ok(result);
    }

    /// <summary>
    /// Top N payment links por volume capturado no período. Mesmo escopo do summary.
    /// </summary>
    [HttpGet("top-payment-links")]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> GetTopPaymentLinks(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? sellerId,
        [FromQuery] PaymentProvider? provider,
        [FromQuery] int limit = 5)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();

        var result = await dashboardService.GetTopPaymentLinksAsync(
            tenantId,
            new DashboardFilterDto(from, to, scopedSellerId, provider),
            Math.Clamp(limit, 1, 50));
        return Ok(result);
    }

    /// <summary>
    /// Top N produtos por volume capturado no período. Resolve cada produto via
    /// <c>Transaction.ExternalReferenceId == "product:{guid}"</c>. Mesmo escopo
    /// (RequireSellerScope) dos demais endpoints de ranking.
    /// </summary>
    [HttpGet("top-products")]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> GetTopProducts(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? sellerId,
        [FromQuery] PaymentProvider? provider,
        [FromQuery] int limit = 5)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();

        var result = await dashboardService.GetTopProductsAsync(
            tenantId,
            new DashboardFilterDto(from, to, scopedSellerId, provider),
            Math.Clamp(limit, 1, 50));
        return Ok(result);
    }

    /// <summary>
    /// Distribuição de tickets em bins fixos (0-50, 50-200, 200-500, 500-1k, 1k-5k, 5k+).
    /// Considera só transações capturadas. Mesmo escopo do summary.
    /// </summary>
    [HttpGet("ticket-distribution")]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> GetTicketDistribution(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? sellerId,
        [FromQuery] PaymentProvider? provider)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();

        var result = await dashboardService.GetTicketDistributionAsync(
            tenantId,
            new DashboardFilterDto(from, to, scopedSellerId, provider));
        return Ok(result);
    }

    /// <summary>
    /// Retenção: clientes únicos no período, quantos já compraram antes (returning),
    /// novos, e quantos repetiram dentro do período. Identifica clientes por PayerEmail.
    /// Olha histórico de até 1 ano antes do From pra classificar returning.
    /// </summary>
    [HttpGet("customer-retention")]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> GetCustomerRetention(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? sellerId,
        [FromQuery] PaymentProvider? provider)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();

        var result = await dashboardService.GetCustomerRetentionAsync(
            tenantId,
            new DashboardFilterDto(from, to, scopedSellerId, provider));
        return Ok(result);
    }

    /// <summary>
    /// Conversão por método de pagamento — capturadas/pendentes/recusadas + taxa de aprovação.
    /// Mesmo escopo do summary.
    /// </summary>
    [HttpGet("conversion")]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> GetConversionByMethod(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? sellerId,
        [FromQuery] PaymentProvider? provider)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();

        var result = await dashboardService.GetConversionByMethodAsync(
            tenantId,
            new DashboardFilterDto(from, to, scopedSellerId, provider));
        return Ok(result);
    }

    /// <summary>
    /// Heatmap dia × hora — agrega transações capturadas por (DayOfWeek, Hour) no UTC.
    /// Mesmo escopo do summary: JWT seller é forçado pro próprio sellerId.
    /// </summary>
    [HttpGet("heatmap")]
    [AuthOrApiKeyAuth]
    public async Task<IActionResult> GetHeatmap(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] Guid? sellerId,
        [FromQuery] PaymentProvider? provider)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();

        var result = await dashboardService.GetHeatmapAsync(
            tenantId,
            new DashboardFilterDto(from, to, scopedSellerId, provider));
        return Ok(result);
    }

    /// <summary>
    /// Returns financial health metrics for the authenticated tenant. Platform-operator
    /// only: requires a JWT with role in {SUPER_ADMIN, OWNER, FINANCE} AND no seller_id
    /// (a seller-OWNER cannot reach this — see AuthInfo.IsPlatformOperator).
    /// </summary>
    [HttpGet("financial-health")]
    [Authorize(Roles = "SUPER_ADMIN,OWNER,FINANCE")]
    public async Task<IActionResult> GetFinancialHealth()
    {
        if (!HttpContext.IsPlatformOperator())
            return StatusCode(StatusCodes.Status403Forbidden);

        Guid tenantId = HttpContext.GetTenantId();
        var result = await dashboardService.GetFinancialHealthAsync(tenantId);
        return Ok(result);
    }
}
