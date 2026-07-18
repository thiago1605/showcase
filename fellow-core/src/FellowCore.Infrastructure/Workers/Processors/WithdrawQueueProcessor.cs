using FellowCore.Application.Modules.Notifications.Interfaces;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

/// <summary>
/// Esvazia a fila FIFO de saques agendados (Payouts PENDING + ScheduledFor &lt;= now).
/// Roda a cada 5 minutos. Cada execução respeita o cap diário Woovi — se um saque
/// excederia o cap, é deixado na fila pra próxima iteração ou rollover diário.
///
/// Não é responsável por retry de saques que FALHARAM no provider — disso cuida o
/// PayoutRetryProcessor existente. Aqui é só "esvaziar fila respeitando cap".
/// </summary>
public class WithdrawQueueProcessor(
    IPayoutRepository payoutRepository,
    IPayoutProcessor payoutProcessor,
    INotificationService notificationService,
    ILogger<WithdrawQueueProcessor> logger) : IWithdrawQueueProcessor
{
    private const int BatchLimit = 100;

    public async Task ProcessAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var dueQueue = await payoutRepository.GetScheduledDueAsync(now, BatchLimit);

        if (dueQueue.Count == 0)
        {
            logger.LogDebug("[WITHDRAW_QUEUE] Fila vazia em {Now}", now);
            return;
        }

        decimal todayTotal = await payoutRepository.GetTodayTotalGrossAsync(now);

        logger.LogInformation(
            "[WITHDRAW_QUEUE] Iniciando processamento — {Count} saque(s) na fila, total já consumido hoje: R${Today}",
            dueQueue.Count, todayTotal);

        int executed = 0;
        int skippedCap = 0;

        foreach (var payout in dueQueue)
        {
            if (ct.IsCancellationRequested) break;

            // Cap check por iteração — outro processo (manual ou outra TX desta mesma
            // execução do processor) pode ter alterado o todayTotal.
            if (todayTotal + payout.Amount > PixLimits.DailyOutboundLimitBusinessHours)
            {
                logger.LogInformation(
                    "[WITHDRAW_QUEUE] Saque {PayoutId} (R${Amount}) excederia cap diário. Mantido na fila pra próxima iteração.",
                    payout.Id, payout.Amount);
                skippedCap++;
                continue;
            }

            try
            {
                payout.MarkAsProcessing();
                payoutRepository.Update(payout);
                await payoutRepository.SaveChangesAsync();

                if (payout.Seller is null)
                {
                    logger.LogWarning("[WITHDRAW_QUEUE] Saque {PayoutId} sem seller carregado — pulando", payout.Id);
                    continue;
                }

                var result = await payoutProcessor.ProcessAsync(payout, payout.Seller);
                if (result.Success)
                {
                    payout.Complete(result.TransactionId!);
                    todayTotal += payout.Amount;
                    executed++;

                    // Producer in-app — outbox enfileira na mesma TX.
                    await notificationService.NotifyPayoutCompletedAsync(
                        payout.TenantId, payout.SellerId, payout.Id, payout.Amount);
                }
                else
                {
                    payout.Fail(result.FailureReason);
                    logger.LogWarning(
                        "[WITHDRAW_QUEUE] Saque {PayoutId} falhou no provider: {Reason}",
                        payout.Id, result.FailureReason);

                    await notificationService.NotifyPayoutFailedAsync(
                        payout.TenantId, payout.SellerId, payout.Id, payout.Amount,
                        result.FailureReason);
                }

                payoutRepository.Update(payout);
                await payoutRepository.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[WITHDRAW_QUEUE] Erro inesperado processando saque {PayoutId}. Marcando pra retry.",
                    payout.Id);
                payout.ScheduleRetry(ex.Message);
                payoutRepository.Update(payout);
                await payoutRepository.SaveChangesAsync();
            }
        }

        logger.LogInformation(
            "[WITHDRAW_QUEUE] Encerrado. Executados: {Executed} | Pulados por cap: {SkippedCap} | Restantes na fila p/ próxima iteração: {Remaining}",
            executed, skippedCap, dueQueue.Count - executed - skippedCap);
    }
}
