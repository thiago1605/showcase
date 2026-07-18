using FellowCore.Application.Common;
using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Splits.Services;

/// <summary>
/// Pure calculation service for split distribution.
/// Rules:
/// - Fixed amounts are allocated first by priority (lowest number = highest priority).
/// - Percentages are applied to the remaining amount after fixed allocations.
/// - Rounding adjustment goes to the highest-priority recipient.
/// </summary>
public class SplitCalculationService : ISplitCalculationService
{
    public SplitCalculationResult Calculate(SplitCalculationInput input)
    {
        if (input.Recipients.Count == 0)
            return new SplitCalculationResult([], 0m, Guid.Empty);

        var totalFee = input.PlatformFee;
        var totalProviderCost = input.ProviderCost;
        var distributableAmount = input.NetAmount;

        // Sort by priority (lower = higher priority)
        var sorted = input.Recipients.OrderBy(r => r.Priority).ToList();

        // Phase 1: Allocate fixed amounts first
        var allocations = new Dictionary<Guid, decimal>();
        decimal remainingForPercent = distributableAmount;

        foreach (var recipient in sorted.Where(r => r.FixedAmount.HasValue))
        {
            var fixedAlloc = Math.Min(recipient.FixedAmount!.Value, remainingForPercent);
            allocations[recipient.SellerId] = fixedAlloc;
            remainingForPercent -= fixedAlloc;
        }

        // Phase 2: Allocate percentages on remaining amount
        foreach (var recipient in sorted.Where(r => r.Percentage.HasValue && !r.FixedAmount.HasValue))
        {
            var percentAlloc = RoundingPolicy.Round(remainingForPercent * recipient.Percentage!.Value / 100m);
            allocations.TryGetValue(recipient.SellerId, out var existing);
            allocations[recipient.SellerId] = existing + percentAlloc;
        }

        // Phase 3: Apply fee allocation policy
        var results = new List<SplitRecipientOutput>();
        decimal totalAllocated = allocations.Values.Sum();

        foreach (var recipient in sorted)
        {
            if (!allocations.TryGetValue(recipient.SellerId, out var grossShare))
                grossShare = 0m;

            decimal feeShare = 0m;
            decimal costShare = 0m;

            switch (input.FeePolicy)
            {
                case FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES:
                    // First recipient (highest priority) pays all fees
                    if (recipient == sorted[0])
                    {
                        feeShare = totalFee;
                        costShare = totalProviderCost;
                    }
                    break;

                case FeeAllocationPolicy.PROPORTIONAL_TO_RECIPIENTS:
                    // Proportional to each recipient's share
                    if (totalAllocated > 0)
                    {
                        var ratio = grossShare / totalAllocated;
                        feeShare = RoundingPolicy.Round(totalFee * ratio);
                        costShare = RoundingPolicy.Round(totalProviderCost * ratio);
                    }
                    break;

                case FeeAllocationPolicy.PLATFORM_ABSORBS:
                    // Platform absorbs all fees — recipients get full gross share
                    feeShare = 0m;
                    costShare = 0m;
                    break;
            }

            var netShare = grossShare - feeShare;
            results.Add(new SplitRecipientOutput(recipient.SellerId, grossShare, feeShare, costShare, netShare));
        }

        // Phase 4: Rounding adjustment — ensure sum of net shares == distributable amount (minus fees if not PLATFORM_ABSORBS)
        decimal expectedTotal = input.FeePolicy == FeeAllocationPolicy.PLATFORM_ABSORBS
            ? totalAllocated
            : totalAllocated - totalFee;
        decimal actualTotal = results.Sum(r => r.NetShare);
        decimal roundingAdj = expectedTotal - actualTotal;

        if (roundingAdj != 0 && results.Count > 0)
        {
            // Apply rounding to highest priority recipient
            var first = results[0];
            results[0] = first with { NetShare = first.NetShare + roundingAdj };
        }

        var primarySellerId = sorted[0].SellerId;
        return new SplitCalculationResult(results, roundingAdj, primarySellerId);
    }
}
