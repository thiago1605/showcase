using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Domain.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Endpoints administrativos pro Modelo Híbrido — reserva de caixa do tenant e
/// limite de antecipação per-seller. Sem isso ativo, toda TX ADVANCE faz fallback
/// INSTALLMENT (estado default seguro).
///
/// Operações ficam atrás de ApiKey do tenant (admin) + audit log.
/// </summary>
[ApiController]
[Route("api/v1/advance-reserve")]
[ApiKeyAuth]
[EnableRateLimiting("fixed")]
public class AdvanceReserveController(
    ITenantRepository tenantRepository,
    ISellerRepository sellerRepository,
    IUnitOfWork unitOfWork,
    ILogger<AdvanceReserveController> logger) : ControllerBase
{
    /// <summary>Lê estado atual da reserve do tenant.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReserve()
    {
        Guid tenantId = HttpContext.GetTenantId();
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
            ?? throw new NotFoundException("Tenant.NotFound", $"Tenant {tenantId} não encontrado.");

        return Ok(new
        {
            tenantId = tenant.Id,
            platformAdvanceReserveCents = tenant.Config?.PlatformAdvanceReserveCents ?? 0,
            platformAdvanceReserveReais = (tenant.Config?.PlatformAdvanceReserveCents ?? 0) / 100m,
        });
    }

    /// <summary>
    /// Top-up da reserve. Admin top-up reflete aporte de caixa real (linha bancária,
    /// capital próprio) que a plataforma topa usar pra antecipar recebíveis.
    /// </summary>
    [HttpPost("topup")]
    [AuditAction("advance_reserve.topup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TopUp([FromBody] AdvanceReserveTopUpDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
            ?? throw new NotFoundException("Tenant.NotFound", $"Tenant {tenantId} não encontrado.");

        if (tenant.Config == null)
            throw new BusinessException("Tenant.NoConfig", "TenantConfig não inicializado.");

        var result = tenant.Config.TopUpAdvanceReserve(request.AmountCents);
        if (result.IsFailure)
            throw new BusinessException(result.Error.Code, result.Error.Description);

        await tenantRepository.SaveChangesAsync();

        logger.LogInformation(
            "[ADVANCE_RESERVE] Top-up de {Cents}c (R${Reais}) realizado pelo tenant {TenantId}. Saldo atual: {Balance}c",
            request.AmountCents, request.AmountCents / 100m, tenantId, tenant.Config.PlatformAdvanceReserveCents);

        return Ok(new
        {
            tenantId = tenant.Id,
            newBalanceCents = tenant.Config.PlatformAdvanceReserveCents,
            newBalanceReais = tenant.Config.PlatformAdvanceReserveCents / 100m,
            addedCents = request.AmountCents,
        });
    }
}

/// <summary>
/// Endpoint do seller limit fica em <c>AdvanceReserveSellersController</c> pra
/// rotear pelo seller ID em vez do tenant.
/// </summary>
[ApiController]
[Route("api/v1/sellers")]
[ApiKeyAuth]
[EnableRateLimiting("fixed")]
public class AdvanceReserveSellersController(
    ISellerRepository sellerRepository,
    ILogger<AdvanceReserveSellersController> logger) : ControllerBase
{
    /// <summary>Lê limit e exposure atual.</summary>
    [HttpGet("{id:guid}/advance-limit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLimit(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var seller = await sellerRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {id} não encontrado.");

        return Ok(new
        {
            sellerId = seller.Id,
            advanceCreditLimit = seller.AdvanceCreditLimit,
            advanceExposureCurrent = seller.AdvanceExposureCurrent,
            availableHeadroom = Math.Max(0m, seller.AdvanceCreditLimit - seller.AdvanceExposureCurrent),
            autoAdvanceSettlement = seller.AutoAdvanceSettlement,
        });
    }

    /// <summary>Admin define o teto de antecipação do seller.</summary>
    [HttpPatch("{id:guid}/advance-limit")]
    [AuditAction("seller.advance_limit_updated")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetLimit(Guid id, [FromBody] SetAdvanceLimitDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var seller = await sellerRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {id} não encontrado.");

        var oldLimit = seller.AdvanceCreditLimit;
        var result = seller.SetAdvanceCreditLimit(request.AdvanceCreditLimit);
        if (result.IsFailure)
            throw new BusinessException(result.Error.Code, result.Error.Description);

        sellerRepository.Update(seller);
        await sellerRepository.SaveChangesAsync();

        logger.LogInformation(
            "Seller {SellerId} AdvanceCreditLimit atualizado: R${Old} → R${New}",
            id, oldLimit, seller.AdvanceCreditLimit);

        return Ok(new
        {
            sellerId = seller.Id,
            advanceCreditLimit = seller.AdvanceCreditLimit,
            advanceExposureCurrent = seller.AdvanceExposureCurrent,
        });
    }

    /// <summary>
    /// Admin define threshold custom de alerta operacional pra exposure do seller.
    /// null = volta pro default global (<c>AdvanceAlert:DefaultSellerExposureThresholdCents</c>).
    /// Útil pra sellers legítimos de alto volume que disparariam o alerta padrão à toa.
    /// </summary>
    [HttpPatch("{id:guid}/advance-alert-threshold")]
    [AuditAction("seller.advance_alert_threshold_updated")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetAlertThreshold(Guid id, [FromBody] SetAlertThresholdDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var seller = await sellerRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {id} não encontrado.");

        var oldThreshold = seller.AdvanceExposureAlertThresholdCents;
        seller.SetAdvanceExposureAlertThreshold(request.ThresholdCents);

        sellerRepository.Update(seller);
        await sellerRepository.SaveChangesAsync();

        logger.LogInformation(
            "Seller {SellerId} AdvanceExposureAlertThreshold atualizado: {Old}c → {New}c",
            id, oldThreshold, seller.AdvanceExposureAlertThresholdCents);

        return Ok(new
        {
            sellerId = seller.Id,
            advanceExposureAlertThresholdCents = seller.AdvanceExposureAlertThresholdCents,
        });
    }
}

public record AdvanceReserveTopUpDto(long AmountCents);
public record SetAdvanceLimitDto(decimal AdvanceCreditLimit);
public record SetAlertThresholdDto(long? ThresholdCents);
