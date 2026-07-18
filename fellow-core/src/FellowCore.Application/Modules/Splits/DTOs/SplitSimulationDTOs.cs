using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Splits.DTOs;

public record SimulateSplitRequest(
    Guid SellerId,
    decimal Amount,
    PaymentType PaymentType,
    int Installments = 1,
    Guid? SplitRuleId = null,
    List<SimulateSplitRecipient>? Splits = null,
    FeeAllocationPolicy? FeeAllocationPolicy = null
);

public record SimulateSplitRecipient(
    Guid SellerId,
    decimal? Amount = null,
    decimal? Percentage = null
);

public record SimulateSplitResponse(
    decimal GrossAmount,
    decimal PlatformFee,
    decimal ProviderCostEstimate,
    decimal PlatformMarginEstimate,
    decimal NetAmount,
    List<SimulatedRecipient> Recipients,
    SimulatedPrimaryResidual PrimaryResidual,
    decimal RoundingAdjustment,
    List<string> Warnings
);

public record SimulatedRecipient(
    Guid SellerId,
    decimal GrossShare,
    decimal FeeShare,
    decimal NetShare,
    string Type
);

public record SimulatedPrimaryResidual(
    Guid SellerId,
    decimal Amount
);
