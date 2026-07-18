using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.Splits.Services;

public class ItemSplitResolver(
    ITransactionItemRepository transactionItemRepository,
    ISplitRuleRepository splitRuleRepository,
    ISplitAllocationRepository splitAllocationRepository,
    IUnitOfWork unitOfWork) : IItemSplitResolver
{
    public async Task<ItemSplitResolution> ResolveFromItemsAsync(Guid tenantId, Guid transactionId)
    {
        var items = await transactionItemRepository.GetByTransactionIdAsync(tenantId, transactionId);
        if (items.Count == 0)
            return new ItemSplitResolution([], HasItemSplits: false);

        // Only process items that have seller or split rule assigned
        var itemsWithSplits = items.Where(i => i.SellerId.HasValue || i.SplitRuleId.HasValue).ToList();
        if (itemsWithSplits.Count == 0)
            return new ItemSplitResolution([], HasItemSplits: false);

        var allocations = new List<SplitAllocation>();
        var recipientAmounts = new Dictionary<Guid, decimal>();

        foreach (var item in itemsWithSplits)
        {
            if (item.SplitRuleId.HasValue)
            {
                // Resolve recipients from split rule
                var rule = await splitRuleRepository.GetByIdWithRecipientsAsync(tenantId, item.SplitRuleId.Value);
                if (rule == null) continue;

                foreach (var recipient in rule.Recipients)
                {
                    decimal amount = 0;
                    if (recipient.FixedAmount.HasValue)
                        amount = Math.Min(recipient.FixedAmount.Value, item.TotalAmount);
                    else if (recipient.Percentage.HasValue)
                        amount = Math.Round(item.TotalAmount * recipient.Percentage.Value / 100m, 2);

                    if (amount <= 0) continue;

                    recipientAmounts.TryAdd(recipient.SellerId, 0);
                    recipientAmounts[recipient.SellerId] += amount;

                    allocations.Add(SplitAllocation.Create(
                        tenantId, transactionId, item.Id,
                        recipient.SellerId, amount, "ITEM_RULE"));
                }
            }
            else if (item.SellerId.HasValue)
            {
                // Direct item → seller allocation
                recipientAmounts.TryAdd(item.SellerId.Value, 0);
                recipientAmounts[item.SellerId.Value] += item.TotalAmount;

                allocations.Add(SplitAllocation.Create(
                    tenantId, transactionId, item.Id,
                    item.SellerId.Value, item.TotalAmount, "EXPLICIT"));
            }
        }

        if (allocations.Count > 0)
        {
            await splitAllocationRepository.AddRangeAsync(allocations);
            await unitOfWork.CommitAsync();
        }

        var aggregated = recipientAmounts
            .Select(kv => new AggregatedRecipient(kv.Key, kv.Value, "ITEM_RULE"))
            .ToList();

        return new ItemSplitResolution(aggregated, HasItemSplits: aggregated.Count > 0);
    }
}
