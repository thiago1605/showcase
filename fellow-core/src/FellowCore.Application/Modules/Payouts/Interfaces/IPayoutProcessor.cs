using FellowCore.Domain.Entities;

namespace FellowCore.Application.Modules.Payouts.Interfaces;

public record PayoutResult(bool Success, string? TransactionId = null, string? FailureReason = null);

public interface IPayoutProcessor
{
    Task<PayoutResult> ProcessAsync(Payout payout, Seller seller);
}
