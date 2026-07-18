using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Splits.DTOs;

// --- Split Rule DTOs ---

public record CreateSplitRuleDto(
    string Name,
    List<SplitRuleRecipientDto> Recipients
);

public record SplitRuleRecipientDto(
    Guid SellerId,
    decimal? Percentage = null,
    decimal? FixedAmount = null,
    int Priority = 0
);

public record SplitRuleResponseDto(
    Guid Id,
    Guid TenantId,
    Guid? OwnerSellerId,
    string? OwnerSellerName,
    string Name,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<SplitRuleRecipientResponseDto> Recipients
);

public record SplitRuleRecipientResponseDto(
    Guid Id,
    Guid SellerId,
    string? SellerName,
    decimal? Percentage,
    decimal? FixedAmount,
    int Priority
);

// --- Split Transfer DTOs ---

public record SplitTransferResponseDto(
    Guid Id,
    Guid TransactionId,
    Guid TenantId,
    Guid RecipientSellerId,
    decimal Amount,
    decimal? Percentage,
    SplitTransferStatus Status,
    string? FailureReason,
    DateTime? ReservedAt,
    DateTime? PaidAt,
    DateTime? ReversedAt,
    DateTime CreatedAt
);
