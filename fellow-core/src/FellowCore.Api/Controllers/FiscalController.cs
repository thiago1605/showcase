using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Fiscal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/fiscal")]
[ApiKeyAuth]
[EnableRateLimiting("fixed")]
public class FiscalController(IFiscalService fiscalService) : ControllerBase
{
    [HttpGet("sellers/{sellerId:guid}/settings")]
    public async Task<IActionResult> GetSettings(Guid sellerId)
    {
        var tenantId = HttpContext.GetTenantId();
        var settings = await fiscalService.GetOrCreateSettingsAsync(tenantId, sellerId);
        return Ok(new
        {
            settings.Id,
            settings.SellerId,
            settings.Enabled,
            settings.MunicipalRegistration,
            settings.ServiceCode,
            settings.IssRate,
            settings.CityCode,
            settings.CreatedAt,
            settings.UpdatedAt
        });
    }

    [HttpPut("sellers/{sellerId:guid}/settings")]
    public async Task<IActionResult> UpdateSettings(Guid sellerId, [FromBody] UpdateFiscalSettingsDto dto)
    {
        var tenantId = HttpContext.GetTenantId();
        var settings = await fiscalService.UpdateSettingsAsync(tenantId, sellerId, dto);
        return Ok(new
        {
            settings.Id,
            settings.SellerId,
            settings.Enabled,
            settings.MunicipalRegistration,
            settings.ServiceCode,
            settings.IssRate,
            settings.CityCode,
            settings.UpdatedAt
        });
    }

    [HttpPost("sellers/{sellerId:guid}/enable")]
    public async Task<IActionResult> Enable(Guid sellerId)
    {
        var tenantId = HttpContext.GetTenantId();
        await fiscalService.EnableAsync(tenantId, sellerId);
        return NoContent();
    }

    [HttpPost("sellers/{sellerId:guid}/disable")]
    public async Task<IActionResult> Disable(Guid sellerId)
    {
        var tenantId = HttpContext.GetTenantId();
        await fiscalService.DisableAsync(tenantId, sellerId);
        return NoContent();
    }

    [HttpPost("transactions/{transactionId:guid}/invoice")]
    public async Task<IActionResult> RequestInvoice(Guid transactionId)
    {
        var tenantId = HttpContext.GetTenantId();
        var invoice = await fiscalService.RequestInvoiceAsync(tenantId, transactionId);
        return Ok(new
        {
            invoice.Id,
            invoice.TransactionId,
            invoice.SellerId,
            Status = invoice.Status.ToString(),
            invoice.Amount,
            invoice.IssAmount,
            invoice.CreatedAt
        });
    }

    [HttpGet("sellers/{sellerId:guid}/invoices")]
    public async Task<IActionResult> GetInvoicesBySeller(Guid sellerId, [FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        var tenantId = HttpContext.GetTenantId();
        var invoices = await fiscalService.GetInvoicesBySellerAsync(tenantId, sellerId, limit, offset);
        return Ok(invoices.Select(i => new
        {
            i.Id,
            i.TransactionId,
            Status = i.Status.ToString(),
            i.Amount,
            i.IssAmount,
            i.InvoiceNumber,
            i.PdfUrl,
            i.CreatedAt,
            i.IssuedAt
        }));
    }
}
