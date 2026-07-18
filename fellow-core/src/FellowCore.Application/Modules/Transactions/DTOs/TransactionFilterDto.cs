using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Transactions.DTOs;

public record TransactionFilterDto(
    int Page = 1,
    int PageSize = 20,
    TransactionStatus? Status = null,
    PaymentType? PaymentType = null,
    PaymentProvider? Provider = null,
    Guid? SellerId = null,
    DateTime? From = null,
    DateTime? To = null,
    // Free-text search across PayerName, PayerEmail e ProviderTransactionId.
    // Quando vazio/null, ignorado. Match é case-insensitive e contém-substring.
    string? Q = null
)
{
    public int NormalizedPage => Math.Max(Page, 1);
    public int Take => Math.Clamp(PageSize, 1, 100);
    public int Skip => (NormalizedPage - 1) * Take;
}
