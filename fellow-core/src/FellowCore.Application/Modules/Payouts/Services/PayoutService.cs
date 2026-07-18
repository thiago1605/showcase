using FellowCore.Application.Common;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Common.Models;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Email.Templates;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Notifications.Interfaces;
using FellowCore.Application.Modules.Payouts.DTOs;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Payouts.Services;

public class PayoutService(
    IPayoutRepository payoutRepository,
    ISellerRepository sellerRepository,
    ITenantRepository tenantRepository,
    ILedgerService ledgerService,
    IPayoutProcessor payoutProcessor,
    IEmailService emailService,
    IRealtimeNotifier realtimeNotifier,
    IBackgroundJobs backgroundJobs,
    IAppMetrics appMetrics,
    INotificationService notificationService,
    ILogger<PayoutService> logger) : IPayoutService
{
    public async Task<PayoutResponseDto> CreateAsync(Guid tenantId, CreatePayoutDto request)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, request.SellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {request.SellerId} não encontrado.");

        if (string.IsNullOrWhiteSpace(seller.ExternalAccountId))
            throw new BusinessException("Seller.NoAccount", "Seller não possui conta BaaS cadastrada para receber payouts.");

        // Calcula fees: fee fixa FellowCore + fee percentual FellowCore (do seller) + fee OpenPix (R$1 se < R$500)
        decimal percentFee = RoundingPolicy.Round(request.Amount * seller.PayoutPercentFee / 100m);
        decimal totalFee = OpenPixPayoutProcessor.CalculateTotalFee(request.Amount, seller.PayoutFixedFee) + percentFee;
        decimal netAmount = request.Amount - totalFee;

        if (netAmount <= 0)
            throw new BusinessException("Payout.FeeExceedsAmount",
                $"O valor solicitado ({request.Amount:F2}) não cobre as taxas ({totalFee:F2}).");

        var result = Payout.Create(tenantId, request.SellerId, request.Amount, totalFee);
        if (result.IsFailure)
            throw new ValidationException(result.Error.Code, result.Error.Description);

        var payout = result.Value;
        payout.MarkAsProcessing();

        // Debita o ledger ANTES de chamar o provider — validação de saldo e débito são atômicos
        // (LedgerService.DebitSellerAsync usa optimistic concurrency com retry)
        // Debit the net payout amount from seller → PLATFORM_PAYOUT
        await ledgerService.DebitSellerAsync(tenantId, request.SellerId, netAmount,
            $"Payout #{payout.Id:N} (líquido: {netAmount:F2})", payout.Id.ToString());

        // L7: Record payout fee as separate ledger entry (debit seller → credit PLATFORM_FEE)
        if (totalFee > 0)
        {
            await ledgerService.DebitPayoutFeeAsync(tenantId, request.SellerId, totalFee,
                $"Taxa Payout #{payout.Id:N}", payout.Id.ToString());
        }

        payoutRepository.Add(payout);
        await payoutRepository.SaveChangesAsync();

        logger.LogInformation(
            "Payout {PayoutId} criado | Seller: {SellerId} | Bruto: {Amount} | Fee: {Fee} (FellowCore fixo: {FellowCoreFixedFee} + FellowCore %: {FellowCorePercentFee} + OpenPix: {OpenPixFee}) | Líquido: {Net}",
            payout.Id, request.SellerId, request.Amount, totalFee, seller.PayoutFixedFee,
            percentFee, totalFee - seller.PayoutFixedFee - percentFee, netAmount);

        try
        {
            var payoutResult = await payoutProcessor.ProcessAsync(payout, seller);

            if (payoutResult.Success)
            {
                payout.Complete(payoutResult.TransactionId!);
                appMetrics.RecordPayout("completed");

                logger.LogInformation("Payout {PayoutId} concluído. TransactionId: {TransactionId}",
                    payout.Id, payoutResult.TransactionId);

                await SendPayoutEmailAsync(tenantId, seller, request.Amount, netAmount, success: true);
                await realtimeNotifier.SendToTenantAsync(tenantId, "payout.completed", new { payoutId = payout.Id, amount = netAmount });

                // In-app notification — outbox vai na mesma TX do payoutRepository.SaveChanges abaixo.
                await notificationService.NotifyPayoutCompletedAsync(
                    tenantId, request.SellerId, payout.Id, netAmount);
            }
            else
            {
                // Provider returned explicit failure — schedule retry or compensate
                await HandlePayoutFailureAsync(payout, payoutResult.FailureReason ?? "Provider returned failure",
                    tenantId, request.SellerId, netAmount, totalFee, seller, request.Amount);
            }
        }
        catch (Exception ex) when (IsTransientException(ex))
        {
            // Transient failure (timeout, network) — schedule retry, keep ledger debit in place
            payout.ScheduleRetry(ex.Message);
            appMetrics.RecordPayoutRetry();
            logger.LogWarning(ex, "Payout {PayoutId} transient failure (attempt {Attempt}/{Max}). Retry scheduled at {RetryAt}",
                payout.Id, payout.AttemptCount, payout.MaxRetries, payout.NextRetryAt);
        }
        catch (Exception ex)
        {
            // Non-transient exception — fail immediately and compensate
            await CompensatePayoutAsync(payout, tenantId, request.SellerId, netAmount, totalFee, ex.Message);
            logger.LogError(ex, "Payout {PayoutId} non-transient failure. Compensated.", payout.Id);
        }

        payoutRepository.Update(payout);
        await payoutRepository.SaveChangesAsync();

        // Event-driven reconciliation after payout completes or fails
        if (payout.Status is Domain.Enums.PayoutStatus.PAID or Domain.Enums.PayoutStatus.FAILED)
        {
            backgroundJobs.Enqueue<IReconciliationService>(
                svc => svc.ReconcilePayoutAsync(tenantId, payout.Id, CancellationToken.None));
        }

        return new PayoutResponseDto(payout.Id, payout.SellerId, payout.Amount, payout.Fee, payout.Status, payout.CreatedAt);
    }

    /// <summary>
    /// Called by PayoutRetryProcessor to retry a payout that previously had a transient failure.
    /// </summary>
    public async Task RetryAsync(Guid payoutId)
    {
        var payout = await payoutRepository.GetByIdGlobalAsync(payoutId)
            ?? throw new NotFoundException("Payout.NotFound", $"Payout {payoutId} não encontrado.");

        if (!payout.IsRetryDue(DateTime.UtcNow))
        {
            logger.LogWarning("Payout {PayoutId} retry called but not due. Status={Status}, NextRetry={NextRetry}",
                payoutId, payout.Status, payout.NextRetryAt);
            return;
        }

        var seller = payout.Seller
            ?? await sellerRepository.GetByIdAsync(payout.TenantId, payout.SellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {payout.SellerId} not found for retry.");

        decimal netAmount = payout.Amount - payout.Fee;
        payout.MarkAsProcessing(); // Increments AttemptCount

        logger.LogInformation("Payout {PayoutId} retry attempt {Attempt}/{Max}",
            payout.Id, payout.AttemptCount, payout.MaxRetries);

        try
        {
            var payoutResult = await payoutProcessor.ProcessAsync(payout, seller);

            if (payoutResult.Success)
            {
                payout.Complete(payoutResult.TransactionId!);
                appMetrics.RecordPayout("completed");
                logger.LogInformation("Payout {PayoutId} completed on retry. TransactionId: {TransactionId}",
                    payout.Id, payoutResult.TransactionId);

                await SendPayoutEmailAsync(payout.TenantId, seller, payout.Amount, netAmount, success: true);
                await realtimeNotifier.SendToTenantAsync(payout.TenantId, "payout.completed", new { payoutId = payout.Id, amount = netAmount });

                await notificationService.NotifyPayoutCompletedAsync(
                    payout.TenantId, payout.SellerId, payout.Id, netAmount);
            }
            else
            {
                await HandlePayoutFailureAsync(payout, payoutResult.FailureReason ?? "Provider returned failure",
                    payout.TenantId, payout.SellerId, netAmount, payout.Fee, seller, payout.Amount);
            }
        }
        catch (Exception ex) when (IsTransientException(ex))
        {
            if (payout.HasExhaustedRetries)
            {
                await CompensatePayoutAsync(payout, payout.TenantId, payout.SellerId, netAmount, payout.Fee, ex.Message);
                logger.LogError(ex, "Payout {PayoutId} exhausted retries. Compensated.", payout.Id);
            }
            else
            {
                payout.ScheduleRetry(ex.Message);
                appMetrics.RecordPayoutRetry();
                logger.LogWarning(ex, "Payout {PayoutId} retry failed (attempt {Attempt}/{Max}). Next retry at {RetryAt}",
                    payout.Id, payout.AttemptCount, payout.MaxRetries, payout.NextRetryAt);
            }
        }
        catch (Exception ex)
        {
            await CompensatePayoutAsync(payout, payout.TenantId, payout.SellerId, netAmount, payout.Fee, ex.Message);
            logger.LogError(ex, "Payout {PayoutId} non-transient failure on retry. Compensated.", payout.Id);
        }

        payoutRepository.Update(payout);
        await payoutRepository.SaveChangesAsync();

        if (payout.Status is Domain.Enums.PayoutStatus.PAID or Domain.Enums.PayoutStatus.FAILED)
        {
            backgroundJobs.Enqueue<IReconciliationService>(
                svc => svc.ReconcilePayoutAsync(payout.TenantId, payout.Id, CancellationToken.None));
        }
    }

    private async Task HandlePayoutFailureAsync(Payout payout, string error, Guid tenantId, Guid sellerId,
        decimal netAmount, decimal totalFee, Seller seller, decimal grossAmount)
    {
        if (!payout.HasExhaustedRetries)
        {
            // Schedule retry — keep ledger debit in place
            payout.ScheduleRetry(error);
            appMetrics.RecordPayoutRetry();
            logger.LogWarning("Payout {PayoutId} failed (attempt {Attempt}/{Max}). Retry at {RetryAt}: {Error}",
                payout.Id, payout.AttemptCount, payout.MaxRetries, payout.NextRetryAt, error);
        }
        else
        {
            // All retries exhausted — compensate
            await CompensatePayoutAsync(payout, tenantId, sellerId, netAmount, totalFee, error);
            await SendPayoutEmailAsync(tenantId, seller, grossAmount, netAmount, success: false, error);
            await realtimeNotifier.SendToTenantAsync(tenantId, "payout.failed", new { payoutId = payout.Id, reason = error });
        }
    }

    private async Task CompensatePayoutAsync(Payout payout, Guid tenantId, Guid sellerId,
        decimal netAmount, decimal totalFee, string reason)
    {
        payout.Fail(reason);
        appMetrics.RecordPayout("failed");
        appMetrics.RecordPayoutFailed();

        // In-app notification — terminal failure path. Outbox vai na mesma TX
        // do SaveChanges no caller.
        await notificationService.NotifyPayoutFailedAsync(
            tenantId, sellerId, payout.Id, netAmount, reason);

        try
        {
            await ledgerService.ReversalCreditAsync(tenantId, sellerId, netAmount,
                $"Estorno Payout #{payout.Id:N} — {reason[..Math.Min(reason.Length, 50)]}", payout.Id.ToString());
            if (totalFee > 0)
                await ledgerService.ReversePayoutFeeAsync(tenantId, sellerId, totalFee,
                    $"Estorno taxa Payout #{payout.Id:N} — falha", payout.Id.ToString());
        }
        catch (Exception reversalEx)
        {
            logger.LogCritical(reversalEx,
                "FALHA CRÍTICA: Não foi possível estornar débito do payout {PayoutId}. Valor: {Amount}. Requer intervenção manual.",
                payout.Id, netAmount);

            // M9: Enqueue background retry for the compensation credit
            backgroundJobs.Enqueue<ILedgerService>(
                svc => svc.ReversalCreditAsync(tenantId, sellerId, netAmount,
                    $"Estorno Payout #{payout.Id:N} — retry compensação", payout.Id.ToString()));
        }
    }

    private static bool IsTransientException(Exception ex) =>
        ex is TimeoutException
        or TaskCanceledException
        or HttpRequestException { StatusCode: System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout or System.Net.HttpStatusCode.TooManyRequests }
        or HttpRequestException { StatusCode: null }; // Network-level failures

    public async Task<PayoutDetailDto> GetByIdAsync(Guid tenantId, Guid id)
    {
        var payout = await payoutRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("Payout.NotFound", $"Payout {id} não encontrado.");

        return MapToDetail(payout);
    }

    public async Task<PagedResult<PayoutDetailDto>> ListAsync(Guid tenantId, PayoutFilterDto filter)
    {
        var (items, totalCount) = await payoutRepository.GetPagedAsync(
            tenantId, filter.Skip, filter.Take, filter.SellerId, filter.Status);

        return new PagedResult<PayoutDetailDto>(
            Items: items.Select(MapToDetail).ToList(),
            TotalCount: totalCount,
            Page: filter.NormalizedPage,
            PageSize: filter.Take);
    }

    private async Task SendPayoutEmailAsync(Guid tenantId, Seller seller, decimal amount, decimal netAmount, bool success, string? failureReason = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(seller.Email)) return;

            var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId);
            var tenantName = tenant?.Name ?? "Fellow Pay";
            var sellerName = seller.TradeName ?? seller.LegalName;

            var message = success
                ? new EmailMessage(
                    To: seller.Email,
                    ToName: sellerName,
                    Subject: $"Saque de {netAmount:C2} realizado com sucesso",
                    HtmlBody: EmailTemplates.PayoutCompleted(tenantName, sellerName, amount, netAmount, DateTime.UtcNow))
                : new EmailMessage(
                    To: seller.Email,
                    ToName: sellerName,
                    Subject: "Falha no processamento do saque",
                    HtmlBody: EmailTemplates.PayoutFailed(tenantName, sellerName, amount, failureReason ?? "Erro desconhecido", DateTime.UtcNow));

            await emailService.SendAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao enviar email de payout para seller {SellerId}", seller.Id);
        }
    }

    private static PayoutDetailDto MapToDetail(Payout p) => new(
        p.Id, p.TenantId, p.SellerId, p.Seller?.TradeName ?? p.Seller?.LegalName,
        p.Amount, p.Fee, p.Status, p.BankTransactionId,
        p.ProcessedAt, p.CreatedAt, p.UpdatedAt);
}
