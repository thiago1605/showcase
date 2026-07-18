using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Modules.Tenants.DTOs;
using FellowCore.Application.Modules.Tenants.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/tenants")]
public class TenantController(ITenantService tenantService, IOpenPixApiClient openPixApi, IConfiguration configuration) : ControllerBase
{
    [HttpPost]
    [MasterKeyAuth]
    [EnableRateLimiting("fixed")]
    public async Task<IActionResult> Create([FromBody] CreateTenantDto dto)
    {
        var result = await tenantService.CreateAsync(dto);
        return Created($"/api/v1/tenants/{result.Tenant.Id}", result);
    }

    [HttpGet("me")]
    [ApiKeyAuth]
    [EnableRateLimiting("fixed")]
    public async Task<IActionResult> GetProfile()
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await tenantService.GetByIdAsync(tenantId);
        return Ok(result);
    }

    [HttpPost("me/rotate-api-key")]
    [ApiKeyAuth]
    [EnableRateLimiting("fixed")]
    [AuditAction("tenant.api_key_rotated")]
    public async Task<IActionResult> RotateApiKey([FromBody] RotateApiKeyDto dto)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await tenantService.RotateApiKeyAsync(tenantId, dto.CurrentApiSecret);
        return Ok(result);
    }

    [HttpPatch("me/providers")]
    [ApiKeyAuth]
    [EnableRateLimiting("fixed")]
    [AuditAction("tenant.providers_updated")]
    public async Task<IActionResult> UpdateProviders([FromBody] UpdateTenantProvidersDto dto)
    {
        Guid tenantId = HttpContext.GetTenantId();
        await tenantService.UpdateProvidersAsync(tenantId, dto);
        return Ok(new { message = "Providers atualizados com sucesso." });
    }

    // ── Provider Webhooks (OpenPix) ─────────────────────────────────

    private string GetAppId() => configuration["OpenPix:AppId"]
        ?? throw new InvalidOperationException("OpenPix:AppId nao configurado.");

    [HttpGet("me/provider-webhooks")]
    [ApiKeyAuth]
    [EnableRateLimiting("fixed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListProviderWebhooks()
    {
        var result = await openPixApi.ListWebhooksAsync(GetAppId());
        return Ok(result.Webhooks.Select(w => new
        {
            id = w.Id,
            name = w.Name,
            url = w.Url,
            isActive = w.IsActive,
            events = w.Events
        }));
    }

    [HttpDelete("me/provider-webhooks/{webhookId}")]
    [ApiKeyAuth]
    [EnableRateLimiting("fixed")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteProviderWebhook(string webhookId)
    {
        await openPixApi.DeleteWebhookAsync(GetAppId(), webhookId);
        return NoContent();
    }
}