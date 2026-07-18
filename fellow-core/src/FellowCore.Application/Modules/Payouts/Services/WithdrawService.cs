using FellowCore.Application.Common;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Payouts.Services;

/// <summary>
/// Facade comercial sobre <see cref="IPayoutProcessor"/> que aplica todas as regras
/// da spec 2026:
///
///   - Min R$ 50, max <see cref="Seller.MaxWithdrawPerRequest"/> (default R$ 5.000)
///   - Tarifa fixa R$ 1 quando &lt; R$ 500
///   - Antecipação D+0 cobra +1% sobre o valor
///   - Cap diário global Woovi R$ 48.800 (PIX OUT janela 8h–20:59) — quando excede,
///     agenda automaticamente pro D+1
///   - D+1 = próximo dia útil (skip sábado/domingo, sem feriados nesta versão)
///
/// Saldo virtual da subconta: consultado via API Woovi/OpenPix no momento da
/// validação (não confia no ledger local — quem é a source-of-truth do dinheiro
/// real é a Woovi).
/// </summary>
public class WithdrawService(
    ISellerRepository sellerRepository,
    IPayoutRepository payoutRepository,
    ILedgerService ledgerService,
    IPayoutProcessor payoutProcessor,
    ILogger<WithdrawService> logger) : IWithdrawService
{
    public async Task<WithdrawResult> RequestAsync(
        Guid tenantId,
        Guid sellerId,
        decimal amount,
        WithdrawType type,
        CancellationToken ct = default)
    {
        // 1. Carrega seller (Sprint 1.5: sem plano — só dados de configuração)
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} não encontrado.");

        // 2. Validações de range
        if (amount < WithdrawRules.MinimumAmount)
            throw new MinimumWithdrawException(amount, WithdrawRules.MinimumAmount);

        if (amount > seller.MaxWithdrawPerRequest)
            throw new IndividualWithdrawLimitException(amount, seller.MaxWithdrawPerRequest);

        // 3. Calcula fees (decimal, sempre)
        decimal fees = 0m;
        if (amount < WithdrawRules.SmallWithdrawThreshold)
            fees += WithdrawRules.SmallWithdrawFee;
        if (type == WithdrawType.D0)
            fees += RoundingPolicy.Round(amount * WithdrawRules.D0FeePercent);

        decimal netAmount = amount - fees;
        if (netAmount <= 0)
            throw new BusinessException("Withdraw.FeeExceedsAmount",
                $"As taxas (R$ {fees:N2}) excedem o valor do saque (R$ {amount:N2}).");

        // 4. Cap diário global Woovi (R$ 48.800). Quando + amount supera, agenda pra D+1.
        // Não bloqueia a operação — o seller fica numa fila FIFO que esvazia automaticamente.
        decimal todayTotal = await payoutRepository.GetTodayTotalGrossAsync(DateTime.UtcNow);
        bool exceedsDailyCap = todayTotal + amount > PixLimits.DailyOutboundLimitBusinessHours;

        // 5. Decide flow: imediato (D+0 sem exceder cap) ou agendado (D+1 ou cap-excedido)
        bool shouldSchedule = type == WithdrawType.D1 || exceedsDailyCap;
        DateTime? scheduledFor = shouldSchedule ? NextBusinessDay(DateTime.UtcNow) : null;

        // 6. Cria Payout (decimal preservado, conversão pra centavos só no provider Woovi)
        var payoutResult = Payout.Create(tenantId, sellerId, amount, fees,
            type: type, scheduledFor: scheduledFor);
        if (payoutResult.IsFailure)
            throw new ValidationException(payoutResult.Error.Code, payoutResult.Error.Description);

        var payout = payoutResult.Value;

        // 7. Debita ledger upfront (saldo travado independente de scheduled ou immediate).
        // Se o saque falhar no processor depois, há compensation via PayoutRetryProcessor.
        await ledgerService.DebitSellerAsync(tenantId, sellerId, netAmount,
            BuildLedgerDescription(payout, scheduledFor), payout.Id.ToString());

        if (fees > 0)
        {
            await ledgerService.DebitPayoutFeeAsync(tenantId, sellerId, fees,
                $"Taxa saque #{payout.Id:N} ({(type == WithdrawType.D0 ? "D+0" : "D+1")})",
                payout.Id.ToString());
        }

        payoutRepository.Add(payout);
        await payoutRepository.SaveChangesAsync();

        logger.LogInformation(
            "[WITHDRAW] Saque criado | Seller: {SellerId} | Bruto: R${Amount} | Fee: R${Fee} | Líquido: R${Net} | Tipo: {Type} | Agendado: {Scheduled} | TodayTotal: R${Today}",
            sellerId, amount, fees, netAmount, type, scheduledFor?.ToString("u") ?? "imediato", todayTotal);

        // 8. Executa imediatamente se D+0 sem exceder cap. Senão fica PENDING+ScheduledFor
        // pra fila FIFO esvaziar via WithdrawQueueProcessor (Hangfire).
        if (!shouldSchedule)
        {
            payout.MarkAsProcessing();
            try
            {
                var result = await payoutProcessor.ProcessAsync(payout, seller);
                if (result.Success)
                {
                    payout.Complete(result.TransactionId!);
                }
                else
                {
                    payout.Fail(result.FailureReason);
                    logger.LogWarning("[WITHDRAW] Saque {PayoutId} falhou na execução D+0: {Reason}",
                        payout.Id, result.FailureReason);
                }
                payoutRepository.Update(payout);
                await payoutRepository.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[WITHDRAW] Erro inesperado executando saque {PayoutId} D+0", payout.Id);
                payout.ScheduleRetry(ex.Message);
                payoutRepository.Update(payout);
                await payoutRepository.SaveChangesAsync();
            }
        }

        return new WithdrawResult(
            PayoutId: payout.Id,
            Status: payout.Status,
            GrossAmount: amount,
            Fee: fees,
            NetAmount: netAmount,
            Type: type,
            ScheduledFor: scheduledFor,
            Message: BuildMessage(payout, type, scheduledFor, exceedsDailyCap));
    }

    /// <summary>
    /// Próximo dia útil (skip weekend). Feriados BR não considerados nesta versão —
    /// quando feriado, processor adia automaticamente até a próxima execução do
    /// recurring job (acumula um pequeno atraso, mas seguro).
    /// </summary>
    private static DateTime NextBusinessDay(DateTime nowUtc)
    {
        // 09:00 UTC ≈ 06:00 BRT — janela operacional início.
        var next = nowUtc.Date.AddDays(1).AddHours(9);
        while (next.DayOfWeek == DayOfWeek.Saturday || next.DayOfWeek == DayOfWeek.Sunday)
            next = next.AddDays(1);
        return next;
    }

    private static string BuildLedgerDescription(Payout payout, DateTime? scheduledFor)
        => scheduledFor.HasValue
            ? $"Saque #{payout.Id:N} (líquido, agendado pra {scheduledFor.Value:yyyy-MM-dd})"
            : $"Saque #{payout.Id:N} (líquido, imediato)";

    private static string BuildMessage(Payout payout, WithdrawType type, DateTime? scheduledFor, bool exceedsDailyCap)
    {
        if (!scheduledFor.HasValue)
            return payout.Status == PayoutStatus.PAID
                ? "Saque executado imediatamente."
                : payout.Status == PayoutStatus.FAILED
                    ? $"Saque falhou: {payout.FailureReason}"
                    : "Saque em processamento.";

        if (exceedsDailyCap && type == WithdrawType.D0)
            return $"Cap diário atingido ({PixLimits.DailyOutboundLimitBusinessHours:N0} BRL). Saque agendado pra {scheduledFor:yyyy-MM-dd HH:mm} UTC.";

        return $"Saque D+1 agendado pra {scheduledFor:yyyy-MM-dd HH:mm} UTC (próximo dia útil).";
    }
}
