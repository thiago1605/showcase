using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Payouts.DTOs;

public record CreatePayoutDto(
    Guid SellerId,
    decimal Amount,
    decimal Fee = 0);

/// <summary>
/// Payload do endpoint comercial <c>POST /api/v1/payouts/withdraw</c>. SellerId
/// é overridable pelo backend quando o requester é seller-scoped (JWT) — neste
/// caso o body é ignorado e o seller do token é usado.
/// </summary>
public record CreateWithdrawDto(
    Guid SellerId,
    decimal Amount,
    /// <summary>Tipo do saque (D+0 instantâneo +1% / D+1 grátis). Default D+1.</summary>
    WithdrawType? Type = null);

public record PayoutResponseDto(
    Guid Id,
    Guid SellerId,
    decimal Amount,
    decimal Fee,
    PayoutStatus Status,
    DateTime CreatedAt);

public record PayoutDetailDto(
    Guid Id,
    Guid TenantId,
    Guid SellerId,
    string? SellerName,
    decimal Amount,
    decimal Fee,
    PayoutStatus Status,
    string? BankTransactionId,
    DateTime? ProcessedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record PayoutFilterDto(int Page = 1, int PageSize = 20, Guid? SellerId = null, PayoutStatus? Status = null)
{
    public int NormalizedPage => Math.Max(Page, 1);
    public int Take => Math.Clamp(PageSize, 1, 100);
    public int Skip => (NormalizedPage - 1) * Take;
}
