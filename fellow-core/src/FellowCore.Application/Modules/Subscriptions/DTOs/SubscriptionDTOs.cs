using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Subscriptions.DTOs;

public record CreateSubscriptionDto(
    Guid SellerId,
    decimal Amount,
    string Description,
    BillingInterval Interval,
    DateTime? StartDate = null,
    Guid? CustomerId = null,
    int? MaxCycles = null
);

public record SubscriptionResponseDto(
    Guid Id,
    Guid SellerId,
    decimal Amount,
    string Description,
    BillingInterval Interval,
    SubscriptionStatus Status,
    DateTime NextBillingDate,
    DateTime CreatedAt
);

public record SubscriptionDetailDto(
    Guid Id,
    Guid TenantId,
    Guid SellerId,
    string? SellerName,
    Guid? CustomerId,
    string? CustomerName,
    decimal Amount,
    string Description,
    BillingInterval Interval,
    SubscriptionStatus Status,
    DateTime StartDate,
    DateTime? EndDate,
    DateTime NextBillingDate,
    int CycleCount,
    int? MaxCycles,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record SubscriptionFilterDto(
    int Page = 1,
    int PageSize = 20,
    Guid? SellerId = null,
    SubscriptionStatus? Status = null)
{
    public int NormalizedPage => Math.Max(Page, 1);
    public int Take => Math.Clamp(PageSize, 1, 100);
    public int Skip => (NormalizedPage - 1) * Take;
}
