using FellowCore.Application.Common;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Reconciliation.Services;

public interface ISettlementReconciliationService
{
    Task RunDailySettlementReconciliationAsync(CancellationToken ct = default);
    Task<SettlementReport> ImportAndReconcileAsync(Guid tenantId, PaymentProvider provider, DateTime periodStart, DateTime periodEnd, CancellationToken ct = default);
}

public class SettlementReconciliationService(
    IEnumerable<ISettlementReportProvider> providers,
    ISettlementReportRepository settlementReportRepository,
    ITransactionRepository transactionRepository,
    IPayoutRepository payoutRepository,
    IReconciliationRepository reconciliationRepository,
    ITenantRepository tenantRepository,
    IAlertService alertService,
    IConfiguration configuration,
    ILogger<SettlementReconciliationService> logger) : ISettlementReconciliationService
{
    public async Task RunDailySettlementReconciliationAsync(CancellationToken ct = default)
    {
        var tenants = await tenantRepository.GetAllAsync();

        foreach (var tenant in tenants)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Determine period: from last import or default 25h
                var periodEnd = DateTime.UtcNow;
                var periodStart = periodEnd.AddHours(-25);

                // Try Stripe
                string? stripeKey = configuration["Stripe:SecretKey"];
                if (!string.IsNullOrEmpty(stripeKey))
                {
                    var lastStripe = await settlementReportRepository.GetLatestAsync(tenant.Id, PaymentProvider.STRIPE);
                    var stripeStart = lastStripe?.PeriodEnd ?? periodStart;

                    await ImportAndReconcileAsync(tenant.Id, PaymentProvider.STRIPE, stripeStart, periodEnd, ct);
                }

                // Try OpenPix if active PIX provider
                string? openpixKey = configuration["OpenPix:AppId"];
                if (!string.IsNullOrEmpty(openpixKey))
                {
                    var lastOpenPix = await settlementReportRepository.GetLatestAsync(tenant.Id, PaymentProvider.OPENPIX);
                    var openpixStart = lastOpenPix?.PeriodEnd ?? periodStart;

                    await ImportAndReconcileAsync(tenant.Id, PaymentProvider.OPENPIX, openpixStart, periodEnd, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[SETTLEMENT_RECON] Falha na reconciliacao de settlement do tenant {TenantId}", tenant.Id);
            }
        }
    }

    public async Task<SettlementReport> ImportAndReconcileAsync(
        Guid tenantId, PaymentProvider provider, DateTime periodStart, DateTime periodEnd,
        CancellationToken ct = default)
    {
        var report = SettlementReport.Create(tenantId, provider, periodStart, periodEnd);
        settlementReportRepository.Add(report);

        logger.LogInformation(
            "[SETTLEMENT_RECON] Importing {Provider} for tenant {TenantId} — {Start:u} to {End:u}",
            provider, tenantId, periodStart, periodEnd);

        try
        {
            // 1. Import items from provider
            var settlementProvider = providers.FirstOrDefault(p => p.Provider == provider)
                ?? throw new InvalidOperationException($"No settlement provider registered for {provider}");

            string apiKey = GetApiKey(provider);

            var items = await settlementProvider.ImportAsync(report.Id, apiKey, periodStart, periodEnd, ct: ct);
            if (items.Count > 0)
                settlementReportRepository.AddItems(items);

            // 2. Match items against internal records
            int matched = 0, mismatched = 0, missingInternal = 0;

            var internalTxs = await transactionRepository.GetByTenantAndDateRangeAsync(
                tenantId, periodStart, periodEnd, provider: provider);
            var txByProviderTxId = internalTxs
                .Where(t => !string.IsNullOrEmpty(t.ProviderTxId))
                .ToDictionary(t => t.ProviderTxId!, t => t);

            var internalPayouts = await payoutRepository.GetByTenantAndDateRangeAsync(tenantId, periodStart, periodEnd);
            var payoutById = internalPayouts.ToDictionary(p => p.Id.ToString(), p => p);

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;

                switch (item.ItemType)
                {
                    case SettlementItemType.CHARGE:
                        MatchCharge(item, txByProviderTxId, ref matched, ref mismatched, ref missingInternal);
                        break;

                    case SettlementItemType.REFUND:
                        MatchRefund(item, txByProviderTxId, ref matched, ref mismatched, ref missingInternal);
                        break;

                    case SettlementItemType.PAYOUT:
                        MatchPayout(item, payoutById, ref matched, ref mismatched, ref missingInternal);
                        break;

                    case SettlementItemType.DISPUTE:
                        MatchDispute(item, txByProviderTxId, ref matched, ref mismatched, ref missingInternal);
                        break;

                    default:
                        // APPLICATION_FEE, ADJUSTMENT, TRANSFER — mark matched (platform-level, no 1:1 internal match expected)
                        item.MarkMatched(item.ProviderTransactionId, item.GrossAmountCents);
                        matched++;
                        break;
                }
            }

            // 3. Reverse check: internal transactions not in settlement report
            int missingExternal = 0;
            var reportChargeSourceIds = items
                .Where(i => i.ItemType == SettlementItemType.CHARGE && i.ChargeId != null)
                .Select(i => i.ChargeId!)
                .ToHashSet();

            foreach (var tx in internalTxs)
            {
                if (tx.Status is TransactionStatus.CAPTURED or TransactionStatus.REFUNDED
                    && !string.IsNullOrEmpty(tx.ProviderTxId)
                    && !reportChargeSourceIds.Contains(tx.ProviderTxId)
                    && items.All(i => i.ProviderTransactionId != tx.ProviderTxId))
                {
                    missingExternal++;
                }
            }

            report.MarkReconciled(matched, mismatched, missingInternal, missingExternal);

            // 4. Create reconciliation issues for mismatches
            if (mismatched > 0 || missingInternal > 0 || missingExternal > 0)
            {
                var run = ReconciliationRun.Create(tenantId, "SETTLEMENT", periodStart, periodEnd);
                reconciliationRepository.AddRun(run);

                foreach (var item in items.Where(i => i.MatchStatus == SettlementItemMatchStatus.MISMATCHED))
                {
                    run.AddIssue(
                        ReconciliationIssueType.AMOUNT_MISMATCH, "CRITICAL",
                        item.InternalTransactionId, item.ProviderTransactionId,
                        item.InternalAmountCents, item.GrossAmountCents,
                        $"Settlement {provider}: {item.MismatchReason}");
                }

                foreach (var item in items.Where(i => i.MatchStatus == SettlementItemMatchStatus.MISSING_INTERNAL))
                {
                    run.AddIssue(
                        ReconciliationIssueType.MISSING_IN_LEDGER, "CRITICAL",
                        null, item.ProviderTransactionId,
                        null, item.GrossAmountCents,
                        $"Settlement {provider}: item {item.ProviderTransactionId} exists in provider but not internally");
                }

                int issues = run.Issues.Count;
                int critical = run.Issues.Count(i => i.Severity == "CRITICAL");
                run.Complete(matched + mismatched, 0, 0, issues, critical, 0);

                await DispatchSettlementAlertsAsync(run, ct);

                logger.LogWarning(
                    "[SETTLEMENT_RECON] {Provider} tenant {TenantId}: {Issues} issues (matched={Matched}, mismatched={Mismatched}, missingInternal={MissingInternal}, missingExternal={MissingExternal})",
                    provider, tenantId, issues, matched, mismatched, missingInternal, missingExternal);
            }
            else
            {
                logger.LogInformation(
                    "[SETTLEMENT_RECON] {Provider} tenant {TenantId}: CLEAN — {Matched} items matched",
                    provider, tenantId, matched);
            }
        }
        catch (Exception ex)
        {
            report.MarkFailed(ex.Message);
            logger.LogError(ex, "[SETTLEMENT_RECON] Falha ao importar/reconciliar {Provider} para tenant {TenantId}",
                provider, tenantId);
        }

        await settlementReportRepository.SaveChangesAsync();
        return report;
    }

    private void MatchCharge(SettlementReportItem item, Dictionary<string, Transaction> txByProviderTxId,
        ref int matched, ref int mismatched, ref int missingInternal)
    {
        // Try to find by charge source ID
        var sourceId = item.ChargeId ?? item.ProviderTransactionId;

        if (txByProviderTxId.TryGetValue(sourceId, out var tx))
        {
            long internalCents = (long)(tx.Amount * 100);
            if (RoundingPolicy.WithinTolerance(item.GrossAmountCents, internalCents))
            {
                item.MarkMatched(tx.Id.ToString(), internalCents);
                matched++;
            }
            else
            {
                item.MarkMismatched(tx.Id.ToString(), internalCents,
                    $"Amount: provider {item.GrossAmountCents} vs internal {internalCents}");
                mismatched++;
            }
        }
        else
        {
            item.MarkMissingInternal();
            missingInternal++;
        }
    }

    private static void MatchRefund(SettlementReportItem item, Dictionary<string, Transaction> txByProviderTxId,
        ref int matched, ref int mismatched, ref int missingInternal)
    {
        // Refunds are negative in Stripe — match by abs value against any refunded transaction
        var refundedTx = txByProviderTxId.Values.FirstOrDefault(t => t.RefundedAmount > 0);
        if (refundedTx != null)
        {
            long internalRefundCents = (long)(refundedTx.RefundedAmount * 100);
            item.MarkMatched(refundedTx.Id.ToString(), internalRefundCents);
            matched++;
        }
        else
        {
            item.MarkMissingInternal();
            missingInternal++;
        }
    }

    private static void MatchPayout(SettlementReportItem item, Dictionary<string, Payout> payoutById,
        ref int matched, ref int mismatched, ref int missingInternal)
    {
        // Try to match by payout source ID
        var matchingPayout = payoutById.Values.FirstOrDefault(p =>
            p.Status == PayoutStatus.PAID && p.BankTransactionId == item.PayoutId);

        if (matchingPayout != null)
        {
            long internalCents = (long)(matchingPayout.Amount * 100);
            if (RoundingPolicy.WithinTolerance(Math.Abs(item.GrossAmountCents), internalCents))
            {
                item.MarkMatched(matchingPayout.Id.ToString(), internalCents);
                matched++;
            }
            else
            {
                item.MarkMismatched(matchingPayout.Id.ToString(), internalCents,
                    $"Payout amount: provider {Math.Abs(item.GrossAmountCents)} vs internal {internalCents}");
                mismatched++;
            }
        }
        else
        {
            item.MarkMissingInternal();
            missingInternal++;
        }
    }

    private static void MatchDispute(SettlementReportItem item, Dictionary<string, Transaction> txByProviderTxId,
        ref int matched, ref int mismatched, ref int missingInternal)
    {
        var disputedTx = txByProviderTxId.Values.FirstOrDefault(t => t.Status == TransactionStatus.CHARGEBACKERROR);
        if (disputedTx != null)
        {
            item.MarkMatched(disputedTx.Id.ToString(), (long)(disputedTx.Amount * 100));
            matched++;
        }
        else
        {
            item.MarkMissingInternal();
            missingInternal++;
        }
    }

    private string GetApiKey(PaymentProvider provider) => provider switch
    {
        PaymentProvider.STRIPE => configuration["Stripe:SecretKey"] ?? throw new InvalidOperationException("Stripe:SecretKey not configured"),
        PaymentProvider.OPENPIX => configuration["OpenPix:AppId"] ?? throw new InvalidOperationException("OpenPix:AppId not configured"),
        PaymentProvider.SANDBOX => "sandbox",
        _ => throw new InvalidOperationException($"No API key configured for {provider}")
    };

    private async Task DispatchSettlementAlertsAsync(ReconciliationRun run, CancellationToken ct)
    {
        var critical = run.Issues.Where(i => i.Severity == "CRITICAL").ToList();
        foreach (var issue in critical)
        {
            try { await alertService.SendCriticalAlertAsync(issue, ct); }
            catch (Exception ex) { logger.LogError(ex, "[SETTLEMENT_RECON] Falha ao enviar alerta critico"); }
        }
    }
}
