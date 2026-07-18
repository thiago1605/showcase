using FellowCore.Application.Common.Models;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.PixPayments.DTOs;
using FellowCore.Application.Modules.PixPayments.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Models;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.PixPayments.Services;

public class PixPaymentService(
    IPixPaymentRepository pixPaymentRepository,
    ITenantRepository tenantRepository,
    IOpenPixApiClient openPixApi,
    IConfiguration configuration,
    ILogger<PixPaymentService> logger) : IPixPaymentService
{
    public async Task<PixPaymentResponseDto> CreateAsync(Guid tenantId, CreatePixPaymentDto request)
    {
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
            ?? throw new NotFoundException("Tenant.NotFound", "Tenant nao encontrado.");

        if (tenant.Config == null)
            throw new BusinessException("Tenant.NoConfig", "Tenant sem configuracao de pagamento ativa.");

        string appId = configuration["OpenPix:AppId"]
            ?? throw new ConfigurationException("OPENPIX_APPID_MISSING", "OpenPix AppId nao configurado.");

        var pixPayment = PixPayment.Create(
            tenantId: tenantId,
            destinationPixKey: request.DestinationPixKey,
            amount: request.Amount,
            provider: tenant.Config.ActivePixProvider,
            description: request.Description
        );

        int valueInCents = (int)(request.Amount * 100);

        var paymentRequest = new OpenPixPaymentRequest(
            Value: valueInCents,
            DestinationAlias: request.DestinationPixKey,
            Comment: request.Description,
            CorrelationId: pixPayment.CorrelationId
        );

        logger.LogInformation("Criando pagamento Pix de {Amount} para {Key}. CorrelationID: {CorrelationId}",
            request.Amount, "***", pixPayment.CorrelationId);

        var response = await openPixApi.CreatePaymentAsync(appId, paymentRequest);

        pixPayment.MarkAsProcessing(response.Payment.TransactionId);

        pixPaymentRepository.Add(pixPayment);
        await pixPaymentRepository.SaveChangesAsync();

        return new PixPaymentResponseDto(
            Id: pixPayment.Id,
            CorrelationId: pixPayment.CorrelationId,
            DestinationPixKey: pixPayment.DestinationPixKey,
            Amount: pixPayment.Amount,
            Status: pixPayment.Status.ToString(),
            Description: pixPayment.Description,
            CreatedAt: pixPayment.CreatedAt
        );
    }

    public async Task<PixPaymentDetailDto> GetByIdAsync(Guid tenantId, Guid id)
    {
        var payment = await pixPaymentRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("PixPayment.NotFound", $"Pagamento Pix {id} nao encontrado.");

        return MapToDetail(payment);
    }

    public async Task<PagedResult<PixPaymentDetailDto>> ListAsync(Guid tenantId, int page, int pageSize)
    {
        var (skip, take, normalizedPage) = PagedResult<PixPaymentDetailDto>.Normalize(page, pageSize);
        var (items, totalCount) = await pixPaymentRepository.GetPagedAsync(tenantId, skip, take);

        return new PagedResult<PixPaymentDetailDto>(
            Items: items.Select(MapToDetail).ToList(),
            TotalCount: totalCount,
            Page: normalizedPage,
            PageSize: take
        );
    }

    private static PixPaymentDetailDto MapToDetail(PixPayment p) => new(
        Id: p.Id,
        CorrelationId: p.CorrelationId,
        DestinationPixKey: p.DestinationPixKey,
        Amount: p.Amount,
        Status: p.Status.ToString(),
        ProviderTransactionId: p.ProviderTransactionId,
        Description: p.Description,
        CreatedAt: p.CreatedAt,
        UpdatedAt: p.UpdatedAt
    );
}
