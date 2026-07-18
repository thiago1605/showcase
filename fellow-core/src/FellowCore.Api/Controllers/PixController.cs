using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Modules.PixPayments.DTOs;
using FellowCore.Application.Modules.PixPayments.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/pix")]
[ApiKeyAuth]
[EnableRateLimiting("fixed")]
public class PixController(
    IPaymentProviderFactory providerFactory,
    IPixPaymentService pixPaymentService,
    IOpenPixApiClient openPixApi,
    Microsoft.Extensions.Configuration.IConfiguration configuration,
    Domain.Interfaces.ITenantRepository tenantRepository) : ControllerBase
{
    [HttpPost("validate-key")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidatePixKey([FromBody] ValidatePixKeyRequest request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId);

        if (tenant?.Config == null)
            return BadRequest(new { error = "Tenant sem configuracao de pagamento ativa." });

        var gateway = providerFactory.GetProvider(tenant.Config.ActivePixProvider);
        var result = await gateway.ValidatePixKeyAsync(tenant, request.PixKey);

        return Ok(new
        {
            key = result.Key,
            type = result.Type,
            ownerName = result.OwnerName,
            ownerDocument = result.OwnerDocument
        });
    }

    [HttpPost("payments")]
    [AuditAction("pix_payment.created")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePayment([FromBody] CreatePixPaymentDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await pixPaymentService.CreateAsync(tenantId, request);
        return CreatedAtAction(nameof(GetPayment), new { id = result.Id }, result);
    }

    [HttpGet("payments/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPayment(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await pixPaymentService.GetByIdAsync(tenantId, id);
        return Ok(result);
    }

    [HttpGet("payments")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPayments(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await pixPaymentService.ListAsync(tenantId, page, pageSize);
        return Ok(result);
    }
    // ── Static QR Codes ──────────────────────────────────────────────

    private string GetAppId() => configuration["OpenPix:AppId"]
        ?? throw new InvalidOperationException("OpenPix:AppId nao configurado.");

    [HttpPost("qr-codes")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateStaticQr([FromBody] CreateStaticQrRequest request)
    {
        var qrRequest = new OpenPixStaticQrRequest(
            Name: request.Name,
            CorrelationId: Guid.NewGuid().ToString(),
            Value: request.Amount.HasValue ? (int)(request.Amount.Value * 100) : null,
            Description: request.Description
        );

        var result = await openPixApi.CreateStaticQrAsync(GetAppId(), qrRequest);
        return Created("", new
        {
            correlationId = result.QrCodeStatic.CorrelationId,
            name = result.QrCodeStatic.Name,
            brCode = result.QrCodeStatic.BrCode ?? result.BrCode,
            qrCodeImage = result.QrCodeStatic.QrCodeImage
        });
    }

    [HttpGet("qr-codes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListStaticQr()
    {
        var result = await openPixApi.ListStaticQrAsync(GetAppId());
        return Ok(result.QrCodes.Select(q => new
        {
            correlationId = q.CorrelationId,
            name = q.Name,
            brCode = q.BrCode,
            qrCodeImage = q.QrCodeImage,
            createdAt = q.CreatedAt
        }));
    }

    [HttpGet("qr-codes/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStaticQr(string id)
    {
        var result = await openPixApi.GetStaticQrAsync(GetAppId(), id);
        return Ok(new
        {
            correlationId = result.QrCodeStatic.CorrelationId,
            name = result.QrCodeStatic.Name,
            identifier = result.QrCodeStatic.Identifier,
            brCode = result.QrCodeStatic.BrCode,
            qrCodeImage = result.QrCodeStatic.QrCodeImage,
            createdAt = result.QrCodeStatic.CreatedAt
        });
    }

    [HttpDelete("qr-codes/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteStaticQr(string id)
    {
        await openPixApi.DeleteStaticQrAsync(GetAppId(), id);
        return NoContent();
    }

    // ── Payment Approve ───────────────────────────────────────────────

    [HttpPost("payments/{correlationId}/approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApprovePayment(string correlationId)
    {
        var result = await openPixApi.ApprovePaymentAsync(GetAppId(),
            new OpenPixPaymentApproveRequest(correlationId));
        return Ok(result.Payment);
    }

    // ── Transfers ────────────────────────────────────────────────────

    [HttpPost("transfers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateTransfer([FromBody] CreatePixTransferRequest request)
    {
        var result = await openPixApi.CreateTransferAsync(GetAppId(),
            new OpenPixTransferRequest(
                Value: (int)(request.Amount * 100),
                FromPixKey: request.FromPixKey,
                ToPixKey: request.ToPixKey,
                CorrelationId: Guid.NewGuid().ToString()));

        return Ok(new
        {
            amount = result.Transaction.Value / 100m,
            time = result.Transaction.Time,
            correlationId = result.Transaction.CorrelationId
        });
    }

    // ── Pix Keys ────────────────────────────────────────────────────

    [HttpGet("keys")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPixKeys()
    {
        var result = await openPixApi.ListPixKeysAsync(GetAppId());
        return Ok(result.PixKeys);
    }

    [HttpPost("keys")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePixKey([FromBody] CreatePixKeyRequest request)
    {
        var result = await openPixApi.CreatePixKeyAsync(GetAppId(),
            new OpenPixPixKeyCreateRequest(request.Key, request.Type));
        return Created("", result);
    }

    [HttpDelete("keys/{pixKey}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePixKey(string pixKey)
    {
        await openPixApi.DeletePixKeyAsync(GetAppId(), pixKey);
        return NoContent();
    }

    [HttpPost("keys/{pixKey}/default")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultPixKey(string pixKey)
    {
        var result = await openPixApi.SetDefaultPixKeyAsync(GetAppId(), pixKey);
        return Ok(result);
    }
}

public record CreatePixKeyRequest(string Key, string Type);
public record CreatePixTransferRequest(decimal Amount, string FromPixKey, string ToPixKey);
