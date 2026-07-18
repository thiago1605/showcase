using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Splits.Interfaces;

/// <summary>
/// Calculates split distribution per recipient, applying fee allocation policy,
/// priority ordering, and rounding adjustment.
/// </summary>
public interface ISplitCalculationService
{
    /// <summary>
    /// Calculate per-recipient breakdown given transaction amounts and split configuration.
    /// </summary>
    SplitCalculationResult Calculate(SplitCalculationInput input);
}

public record SplitCalculationInput(
    decimal GrossAmount,
    decimal NetAmount,
    decimal PlatformFee,
    decimal ProviderCost,
    List<SplitRecipientInput> Recipients,
    FeeAllocationPolicy FeePolicy
);

public record SplitRecipientInput(
    Guid SellerId,
    decimal? FixedAmount,
    decimal? Percentage,
    int Priority
);

public record SplitCalculationResult(
    List<SplitRecipientOutput> Recipients,
    decimal RoundingAdjustment,
    Guid PrimarySellerId
);

public record SplitRecipientOutput(
    Guid SellerId,
    decimal GrossShare,
    decimal FeeShare,
    decimal ProviderCostShare,
    decimal NetShare
);
