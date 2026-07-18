using FellowCore.Application.Common.Models;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Splits.DTOs;
using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Splits.Services;

public class SplitRuleService(
    ISplitRuleRepository splitRuleRepository,
    ISellerRepository sellerRepository,
    ILogger<SplitRuleService> logger) : ISplitRuleService
{
    public async Task<SplitRuleResponseDto> CreateAsync(Guid tenantId, CreateSplitRuleDto request, Guid? ownerSellerId = null)
    {
        if (request.Recipients.Count == 0)
            throw new ValidationException("SplitRule.NoRecipients", "A regra de split deve ter ao menos um destinatario.");

        if (request.Recipients.Count > 50)
            throw new ValidationException("SplitRule.TooManyRecipients", "A regra de split pode ter no maximo 50 destinatarios.");

        // Reject duplicate recipient sellers — the rule has no defined behavior when the
        // same seller appears twice and the engine would compute conflicting shares.
        var distinctRecipientCount = request.Recipients.Select(r => r.SellerId).Distinct().Count();
        if (distinctRecipientCount != request.Recipients.Count)
            throw new ValidationException("SplitRule.DuplicateRecipient", "Há destinatário duplicado na regra de split.");

        var exists = await splitRuleRepository.ExistsByNameAsync(tenantId, request.Name);
        if (exists)
            throw new ConflictException("SplitRule.NameAlreadyExists", $"Ja existe uma regra de split ativa com o nome '{request.Name}'.");

        // Validate all sellers exist and belong to the tenant
        foreach (var recipient in request.Recipients)
        {
            var seller = await sellerRepository.GetByIdAsync(tenantId, recipient.SellerId);
            if (seller == null)
                throw new NotFoundException("SplitRule.SellerNotFound", $"Seller {recipient.SellerId} nao encontrado.");
        }

        // If the caller is a seller (JWT), the owner must be that seller. The controller
        // is responsible for forcing the value before reaching here; we just thread it.
        if (ownerSellerId.HasValue && ownerSellerId.Value != Guid.Empty)
        {
            var ownerSeller = await sellerRepository.GetByIdAsync(tenantId, ownerSellerId.Value);
            if (ownerSeller == null)
                throw new NotFoundException("SplitRule.OwnerSellerNotFound", $"Owner seller {ownerSellerId} nao encontrado no tenant.");
        }

        var result = SplitRule.Create(tenantId, request.Name, ownerSellerId);
        if (result.IsFailure)
            throw new ValidationException(result.Error.Code, result.Error.Description);

        var rule = result.Value;

        foreach (var recipient in request.Recipients)
        {
            var addResult = rule.AddRecipient(recipient.SellerId, recipient.Percentage, recipient.FixedAmount, recipient.Priority);
            if (addResult.IsFailure)
                throw new ValidationException(addResult.Error.Code, addResult.Error.Description);
        }

        var validationResult = rule.ValidateRecipientTotals();
        if (validationResult.IsFailure)
            throw new ValidationException(validationResult.Error.Code, validationResult.Error.Description);

        splitRuleRepository.Add(rule);
        await splitRuleRepository.SaveChangesAsync();

        logger.LogInformation("SplitRule {RuleId} created for tenant {TenantId} with {Count} recipients",
            rule.Id, tenantId, request.Recipients.Count);

        var nameMap = await BuildSellerNameMapAsync(tenantId, [rule]);
        return MapToDto(rule, nameMap);
    }

    public async Task<SplitRuleResponseDto> GetByIdAsync(Guid tenantId, Guid id)
    {
        var rule = await splitRuleRepository.GetByIdWithRecipientsAsync(tenantId, id)
            ?? throw new NotFoundException("SplitRule.NotFound", $"Regra de split {id} nao encontrada.");

        var nameMap = await BuildSellerNameMapAsync(tenantId, [rule]);
        return MapToDto(rule, nameMap);
    }

    public async Task<PagedResult<SplitRuleResponseDto>> ListAsync(Guid tenantId, int page, int pageSize, Guid? ownerOrRecipientSellerId = null)
    {
        var (skip, take, normalizedPage) = PagedResult<SplitRuleResponseDto>.Normalize(page, pageSize);
        var (items, totalCount) = ownerOrRecipientSellerId.HasValue
            ? await splitRuleRepository.GetPagedByOwnerOrRecipientAsync(tenantId, ownerOrRecipientSellerId.Value, skip, take)
            : await splitRuleRepository.GetPagedAsync(tenantId, skip, take);

        var nameMap = await BuildSellerNameMapAsync(tenantId, items);

        return new PagedResult<SplitRuleResponseDto>(
            Items: items.Select(r => MapToDto(r, nameMap)).ToList(),
            TotalCount: totalCount,
            Page: normalizedPage,
            PageSize: take
        );
    }

    public async Task ActivateAsync(Guid tenantId, Guid id)
    {
        var rule = await splitRuleRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("SplitRule.NotFound", $"Regra de split {id} nao encontrada.");

        // Idempotent: activating an already-active rule is a no-op (the controller still
        // returns 204). Avoids surfacing a confusing error if two clients click at once.
        if (rule.IsActive) return;

        rule.Activate();
        splitRuleRepository.Update(rule);
        await splitRuleRepository.SaveChangesAsync();

        logger.LogInformation("SplitRule {RuleId} activated for tenant {TenantId}", id, tenantId);
    }

    public async Task DeactivateAsync(Guid tenantId, Guid id)
    {
        var rule = await splitRuleRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("SplitRule.NotFound", $"Regra de split {id} nao encontrada.");

        rule.Deactivate();
        splitRuleRepository.Update(rule);
        await splitRuleRepository.SaveChangesAsync();

        logger.LogInformation("SplitRule {RuleId} deactivated for tenant {TenantId}", id, tenantId);
    }

    // Coleta todos os sellerIds (owner + recipients) que aparecem nas regras
    // e busca o display name em uma única query, pra evitar N+1 ao mapear DTOs.
    private async Task<IReadOnlyDictionary<Guid, string>> BuildSellerNameMapAsync(Guid tenantId, IEnumerable<SplitRule> rules)
    {
        var ids = new HashSet<Guid>();
        foreach (var rule in rules)
        {
            if (rule.OwnerSellerId.HasValue) ids.Add(rule.OwnerSellerId.Value);
            foreach (var r in rule.Recipients) ids.Add(r.SellerId);
        }
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        var sellers = await sellerRepository.GetByIdsAsync(tenantId, ids);
        return sellers.ToDictionary(
            s => s.Id,
            s => string.IsNullOrWhiteSpace(s.TradeName) ? s.LegalName : s.TradeName!
        );
    }

    private static SplitRuleResponseDto MapToDto(SplitRule rule, IReadOnlyDictionary<Guid, string> nameMap) => new(
        Id: rule.Id,
        TenantId: rule.TenantId,
        OwnerSellerId: rule.OwnerSellerId,
        OwnerSellerName: rule.OwnerSellerId.HasValue && nameMap.TryGetValue(rule.OwnerSellerId.Value, out var ownerName) ? ownerName : null,
        Name: rule.Name,
        IsActive: rule.IsActive,
        CreatedAt: rule.CreatedAt,
        UpdatedAt: rule.UpdatedAt,
        Recipients: rule.Recipients.Select(r => new SplitRuleRecipientResponseDto(
            Id: r.Id,
            SellerId: r.SellerId,
            SellerName: nameMap.TryGetValue(r.SellerId, out var name) ? name : null,
            Percentage: r.Percentage,
            FixedAmount: r.FixedAmount,
            Priority: r.Priority
        )).OrderBy(r => r.Priority).ToList()
    );
}
