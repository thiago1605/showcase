using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public interface IRefundRetryProcessor
{
    Task ProcessAsync(CancellationToken ct = default);
}

public class RefundRetryProcessor(
    IRefundIntentRepository refundIntentRepository,
    ITransactionRepository transactionRepository,
    ITenantRepository tenantRepository,
    ISellerRepository sellerRepository,
    IRailRouter railRouter,
    ILedgerService ledgerService,
    IBackgroundJobs backgroundJobs,
    IAppMetrics appMetrics,
    ILogger<RefundRetryProcessor> logger) : IRefundRetryProcessor
{
    public async Task ProcessAsync(CancellationToken ct = default)
    {
        var intents = await refundIntentRepository.GetRetryDueAsync(DateTime.UtcNow);

        if (intents.Count == 0) return;

        logger.LogInformation("[REFUND_RETRY] Processing {Count} refund intent(s) due for retry", intents.Count);

        foreach (var intent in intents)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await RetryRefundAsync(intent);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[REFUND_RETRY] Failed to retry refund intent {IntentId}", intent.Id);
            }
        }
    }

    private async Task RetryRefundAsync(RefundIntent intent)
    {
        // IMPORTANTE: usar GetByIdWithTimelineAsync (Include de Timeline) em vez do
        // intent.Transaction navigation. Quando Stripe sucede aqui, vamos chamar
        // `transaction.Refund(amount)` — que pode disparar `UpdateStatus(REFUNDED)`
        // e adicionar um TransactionEvent na coleção Timeline. Sem o Include, EF
        // não rastreia a coleção: o novo evento é classificado como Modified ao
        // invés de Added, gerando UPDATE em linha inexistente e DbUpdateConcurrencyException.
        // Mesmo bug que afetava TransactionService.RefundAsync antes do fix.
        var transaction = await transactionRepository.GetByIdWithTimelineAsync(
            intent.TenantId, intent.TransactionId);

        if (transaction == null)
        {
            logger.LogError("[REFUND_RETRY] Transaction {TxId} not found for refund intent {IntentId}",
                intent.TransactionId, intent.Id);
            intent.Fail();
            refundIntentRepository.Update(intent);
            await refundIntentRepository.SaveChangesAsync();
            return;
        }

        var rail = railRouter.ResolveRailForTransaction(transaction);
        var tenant = await tenantRepository.GetByIdWithConfigAsync(intent.TenantId);
        if (tenant == null)
        {
            intent.Fail();
            refundIntentRepository.Update(intent);
            await refundIntentRepository.SaveChangesAsync();
            return;
        }

        Seller? seller = transaction.SellerId.HasValue
            ? await sellerRepository.GetByIdAsync(intent.TenantId, transaction.SellerId.Value)
            : null;

        intent.MarkProcessing(); // Increments AttemptCount

        logger.LogInformation("[REFUND_RETRY] Retry attempt {Attempt}/{Max} for refund intent {IntentId} (TX: {TxId})",
            intent.AttemptCount, intent.MaxRetries, intent.Id, intent.TransactionId);

        try
        {
            var refundId = await rail.ExecuteRefundAsync(
                tenant, seller, transaction.ProviderTxId!, intent.Amount, intent.Reason, intent.IdempotencyKey);

            // Provider sucesso. Mutamos a Transaction ANTES de marcar intent COMPLETED
            // pra manter os dois estados em sincronia. Se o domain rejeitar (ex: outro
            // refund concorrente subiu RefundedAmount), logamos CRITICAL pra reconciliação
            // manual — Stripe já efetivou o estorno, não dá pra desfazer aqui.
            var refundResult = transaction.Refund(intent.Amount);
            if (refundResult.IsFailure)
            {
                logger.LogCritical(
                    "[REFUND_RETRY] CRITICAL: Refund of {Amount} succeeded at provider ({RefundId}) but domain rejected: {Error}. TX: {TxId}. Manual intervention required.",
                    intent.Amount, refundId, refundResult.Error.Description, intent.TransactionId);
                intent.Fail();
                refundIntentRepository.Update(intent);
                await refundIntentRepository.SaveChangesAsync();

                // Enfileira reconciliação pra resolver o desalinhamento
                backgroundJobs.Enqueue<IReconciliationService>(
                    svc => svc.ReconcileTransactionAsync(intent.TenantId, intent.TransactionId, CancellationToken.None));
                return;
            }

            intent.Complete(refundId);
            appMetrics.RecordRefund();

            // Mesma quebra usada no caminho síncrono (TransactionService.RefundAsync).
            // Single source of truth via RefundCalculator — qualquer divergência
            // entre os dois caminhos seria bug de drift, exatamente o que esse
            // refactor elimina.
            var breakdown = RefundCalculator.Calculate(transaction, intent.Amount);

            logger.LogInformation(
                "[REFUND_RETRY] Refund intent {IntentId} completed on retry. RefundId: {RefundId}. Total refunded: {Refunded}/{Total}. Seller debit: R${SellerDebit}",
                intent.Id, refundId, transaction.RefundedAmount, transaction.Amount, breakdown.SellerTotalDebit);

            // Debit seller: GROSS INTEGRAL (política nova). Plataforma fica
            // com margem pra cobrir custo do provider não recuperável.
            if (transaction.SellerId.HasValue)
            {
                try
                {
                    await ledgerService.DebitSellerAsync(intent.TenantId, transaction.SellerId.Value, breakdown.SellerTotalDebit,
                        $"Reembolso TX #{intent.TransactionId:N} (RefundId: {refundId}) [retry] — gross R${breakdown.SellerTotalDebit} (líquido R${breakdown.SellerNetPortion} + taxa R${breakdown.PlatformFeeWithheld})",
                        $"REFUND_{intent.TransactionId}_{refundId}");
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex,
                        "[REFUND_RETRY] CRITICAL: Refund processed on provider but ledger not debited. TX: {TxId}, Amount: {Amount}",
                        intent.TransactionId, breakdown.SellerTotalDebit);
                }
            }
        }
        catch (Exception ex) when (IsTransientException(ex))
        {
            if (intent.HasExhaustedRetries)
            {
                intent.Fail();
                logger.LogError(ex, "[REFUND_RETRY] Refund intent {IntentId} exhausted retries. Marking FAILED.", intent.Id);

                // Create reconciliation issue for manual resolution
                backgroundJobs.Enqueue<IReconciliationService>(
                    svc => svc.ReconcileTransactionAsync(intent.TenantId, intent.TransactionId, CancellationToken.None));
            }
            else
            {
                intent.ScheduleRetry(ex.Message);
                appMetrics.RecordRefundRetry();
                logger.LogWarning(ex, "[REFUND_RETRY] Refund intent {IntentId} retry failed (attempt {Attempt}/{Max}). Next at {RetryAt}",
                    intent.Id, intent.AttemptCount, intent.MaxRetries, intent.NextRetryAt);
            }
        }
        catch (Exception ex)
        {
            intent.Fail();
            logger.LogError(ex, "[REFUND_RETRY] Refund intent {IntentId} non-transient failure. Marking FAILED.", intent.Id);
        }

        refundIntentRepository.Update(intent);
        await refundIntentRepository.SaveChangesAsync();
    }

    private static bool IsTransientException(Exception ex) =>
        ex is TimeoutException
        or TaskCanceledException
        or HttpRequestException { StatusCode: System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout or System.Net.HttpStatusCode.TooManyRequests }
        or HttpRequestException { StatusCode: null };
}
