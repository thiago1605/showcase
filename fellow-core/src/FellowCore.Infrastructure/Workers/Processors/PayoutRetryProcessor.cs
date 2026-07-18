using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public interface IPayoutRetryProcessor
{
    Task ProcessAsync(CancellationToken ct = default);
}

public class PayoutRetryProcessor(
    IPayoutRepository payoutRepository,
    IPayoutService payoutService,
    ILogger<PayoutRetryProcessor> logger) : IPayoutRetryProcessor
{
    public async Task ProcessAsync(CancellationToken ct = default)
    {
        var payouts = await payoutRepository.GetRetryDueAsync(DateTime.UtcNow);

        if (payouts.Count == 0) return;

        logger.LogInformation("[PAYOUT_RETRY] Processing {Count} payout(s) due for retry", payouts.Count);

        foreach (var payout in payouts)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await payoutService.RetryAsync(payout.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[PAYOUT_RETRY] Failed to retry payout {PayoutId}", payout.Id);
            }
        }
    }
}
