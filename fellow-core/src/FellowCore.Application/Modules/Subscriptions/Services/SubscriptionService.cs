using FellowCore.Application.Common.Models;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Subscriptions.DTOs;
using FellowCore.Application.Modules.Subscriptions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Subscriptions.Services;

public class SubscriptionService(
    ISubscriptionRepository subscriptionRepository,
    ISellerRepository sellerRepository,
    ILogger<SubscriptionService> logger) : ISubscriptionService
{
    public async Task<SubscriptionResponseDto> CreateAsync(Guid tenantId, CreateSubscriptionDto request)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, request.SellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {request.SellerId} nao encontrado.");

        if (seller.Status != SellerStatus.ACTIVE)
            throw new BusinessException("Seller.NotActive", $"Seller {seller.Id} nao esta ativo (status: {seller.Status}).");

        var result = Subscription.Create(
            tenantId,
            request.SellerId,
            request.Amount,
            request.Description,
            request.Interval,
            request.StartDate,
            request.CustomerId,
            request.MaxCycles);

        if (result.IsFailure)
            throw new ValidationException(result.Error.Code, result.Error.Description);

        var subscription = result.Value;

        subscriptionRepository.Add(subscription);
        await subscriptionRepository.SaveChangesAsync();

        logger.LogInformation(
            "Subscription {SubscriptionId} criada | Seller: {SellerId} | Valor: {Amount} | Intervalo: {Interval}",
            subscription.Id, request.SellerId, request.Amount, request.Interval);

        return MapToResponse(subscription);
    }

    public async Task<SubscriptionDetailDto> GetByIdAsync(Guid tenantId, Guid id)
    {
        var subscription = await subscriptionRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("Subscription.NotFound", $"Assinatura {id} não encontrada.");

        return MapToDetail(subscription);
    }

    public async Task<PagedResult<SubscriptionDetailDto>> ListAsync(Guid tenantId, SubscriptionFilterDto filter)
    {
        var (items, totalCount) = await subscriptionRepository.GetPagedAsync(
            tenantId, filter.Skip, filter.Take, filter.SellerId, filter.Status);

        return new PagedResult<SubscriptionDetailDto>(
            Items: items.Select(MapToDetail).ToList(),
            TotalCount: totalCount,
            Page: filter.NormalizedPage,
            PageSize: filter.Take);
    }

    public async Task<SubscriptionDetailDto> CancelAsync(Guid tenantId, Guid id)
    {
        var subscription = await subscriptionRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("Subscription.NotFound", $"Assinatura {id} não encontrada.");

        subscription.Cancel();

        subscriptionRepository.Update(subscription);
        await subscriptionRepository.SaveChangesAsync();

        logger.LogInformation("Subscription {SubscriptionId} cancelada", id);

        return MapToDetail(subscription);
    }

    public async Task<SubscriptionDetailDto> PauseAsync(Guid tenantId, Guid id)
    {
        var subscription = await subscriptionRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("Subscription.NotFound", $"Assinatura {id} não encontrada.");

        if (subscription.Status != Domain.Enums.SubscriptionStatus.ACTIVE)
            throw new BusinessException("Subscription.NotActive", "Apenas assinaturas ativas podem ser pausadas.");

        subscription.Pause();

        subscriptionRepository.Update(subscription);
        await subscriptionRepository.SaveChangesAsync();

        logger.LogInformation("Subscription {SubscriptionId} pausada", id);

        return MapToDetail(subscription);
    }

    public async Task<SubscriptionDetailDto> ResumeAsync(Guid tenantId, Guid id)
    {
        var subscription = await subscriptionRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("Subscription.NotFound", $"Assinatura {id} não encontrada.");

        if (subscription.Status != Domain.Enums.SubscriptionStatus.PAUSED)
            throw new BusinessException("Subscription.NotPaused", "Apenas assinaturas pausadas podem ser retomadas.");

        subscription.Resume();

        subscriptionRepository.Update(subscription);
        await subscriptionRepository.SaveChangesAsync();

        logger.LogInformation("Subscription {SubscriptionId} retomada", id);

        return MapToDetail(subscription);
    }

    private static SubscriptionResponseDto MapToResponse(Subscription s) => new(
        s.Id, s.SellerId, s.Amount, s.Description, s.Interval,
        s.Status, s.NextBillingDate, s.CreatedAt);

    private static SubscriptionDetailDto MapToDetail(Subscription s) => new(
        s.Id, s.TenantId, s.SellerId,
        s.Seller?.TradeName ?? s.Seller?.LegalName,
        s.CustomerId,
        s.Customer?.Name,
        s.Amount, s.Description, s.Interval, s.Status,
        s.StartDate, s.EndDate, s.NextBillingDate,
        s.CycleCount, s.MaxCycles,
        s.CreatedAt, s.UpdatedAt);
}
