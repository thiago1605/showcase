using FellowCore.Application.Modules.Payouts.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public interface IWithdrawalResumeProcessor
{
    Task ProcessAsync(CancellationToken ct = default);
}

/// <summary>
/// Resume de attempts que ficaram em PENDING/IN_PROGRESS após crash do API
/// ou término de request antes de completar todos os steps. Hangfire roda
/// minutely; cada attempt é re-passado pelo <see cref="IWithdrawOrchestrator"/>
/// que detecta steps COMPLETED (skip) vs PENDING (executa).
///
/// Resiliência:
///   - Idempotency-key dos steps garante que retry não duplica payout no provider
///   - Multiple workers competindo pelo mesmo attempt resolve via row-level lock
///     (cada UPDATE marca step IN_PROGRESS — UPDATE concorrente cai num xmin conflict)
/// </summary>
public class WithdrawalResumeProcessor(
    IWithdrawalAttemptRepository attemptRepo,
    IWithdrawOrchestrator orchestrator,
    ILogger<WithdrawalResumeProcessor> logger) : IWithdrawalResumeProcessor
{
    public async Task ProcessAsync(CancellationToken ct = default)
    {
        var unfinished = await attemptRepo.GetUnfinishedAsync(50);
        if (unfinished.Count == 0) return;

        logger.LogInformation("[WITHDRAW_RESUME] Processing {N} unfinished attempts", unfinished.Count);

        foreach (var attempt in unfinished)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await orchestrator.ResumeAsync(attempt, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[WITHDRAW_RESUME] Falha resumindo attempt {AttemptId}", attempt.Id);
            }
        }
    }
}
