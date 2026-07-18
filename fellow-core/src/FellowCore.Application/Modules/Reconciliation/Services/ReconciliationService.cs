using FellowCore.Application.Common;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Models;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Reconciliation.Services;

public class ReconciliationService(
    IReconciliationRepository reconciliationRepository,
    ILedgerRepository ledgerRepository,
    ITransactionRepository transactionRepository,
    IPayoutRepository payoutRepository,
    IDisputeRepository disputeRepository,
    IRefundIntentRepository refundIntentRepository,
    ISplitTransferRepository splitTransferRepository,
    ISellerRepository sellerRepository,
    IStripeApiClient stripeApiClient,
    ITenantRepository tenantRepository,
    ILedgerService ledgerService,
    IAlertService alertService,
    IConfiguration configuration,
    ILogger<ReconciliationService> logger) : IReconciliationService
{
    // ── Batch entry point ────────────────────────────────────────────────

    public async Task RunDailyReconciliationAsync(CancellationToken ct = default)
    {
        var tenants = await tenantRepository.GetAllAsync();

        foreach (var tenant in tenants)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await RunTenantReconciliationAsync(tenant, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[RECON] Falha na reconciliacao do tenant {TenantId}", tenant.Id);
            }
        }
    }

    private async Task RunTenantReconciliationAsync(Tenant tenant, CancellationToken ct)
    {
        string? apiKey = configuration["Stripe:SecretKey"];

        // Determine period: incremental from last run, or default 25h
        var lastRun = await reconciliationRepository.GetLatestRunAsync(tenant.Id, "BATCH");
        DateTime periodStart = lastRun?.PeriodEnd ?? DateTime.UtcNow.AddHours(-25);
        DateTime periodEnd = DateTime.UtcNow;

        // Also run a rolling backfill (last 7 days) to catch late webhooks
        DateTime backfillStart = DateTime.UtcNow.AddDays(-7);
        if (periodStart > backfillStart) periodStart = backfillStart;

        var run = ReconciliationRun.Create(tenant.Id, "BATCH", periodStart, periodEnd);
        reconciliationRepository.AddRun(run);

        logger.LogInformation(
            "[RECON:{RunId}] Tenant {TenantId} — periodo {Start:u} a {End:u}",
            run.Id, tenant.Id, periodStart, periodEnd);

        int transactionsChecked = 0;
        int payoutsChecked = 0;
        int ledgerAccountsChecked = 0;

        try
        {
            // Phase 1: Ledger internal consistency
            ledgerAccountsChecked = await ReconcileLedgerInternalAsync(run, ct);

            // Phase 2: Transaction-level 1:1 reconciliation
            if (!string.IsNullOrEmpty(apiKey))
                transactionsChecked = await ReconcileTransactionsAsync(run, tenant.Id, apiKey, periodStart, periodEnd, ct);

            // Phase 3: Payout reconciliation
            payoutsChecked = await ReconcilePayoutsAsync(run, tenant.Id, apiKey, periodStart, periodEnd, ct);

            // Phase 4: Platform balance drift
            if (!string.IsNullOrEmpty(apiKey))
                await ReconcilePlatformBalanceAsync(run, tenant.Id, apiKey, ct);

            // Phase 5: Cross-rail invariants (V2)
            await ReconcileCrossRailInvariantsAsync(run, tenant.Id, periodStart, periodEnd, ct);

            // Phase 6: Split integrity (V3)
            await ReconcileSplitsAsync(run, tenant.Id, periodStart, periodEnd, ct);

            // Phase 7: Stuck operations (V4)
            await ReconcileStuckOperationsAsync(run, tenant.Id, ct);

            // Phase 8: Operational maturity checks (V5)
            await ReconcileOperationalChecksAsync(run, tenant.Id, ct);

            int issuesFound = run.Issues.Count;
            int criticalIssues = run.Issues.Count(i => i.Severity == "CRITICAL");
            long platformDrift = run.Issues
                .Where(i => i.Type == ReconciliationIssueType.PLATFORM_BALANCE_DRIFT)
                .Select(i => i.DriftCents ?? 0).FirstOrDefault();

            run.Complete(transactionsChecked, payoutsChecked, ledgerAccountsChecked,
                issuesFound, criticalIssues, platformDrift);

            LogRunSummary(run);
            await DispatchAlertsAsync(run, ct);
        }
        catch (Exception ex)
        {
            run.Fail(ex.Message);
            logger.LogError(ex, "[RECON:{RunId}] Erro fatal na reconciliacao", run.Id);
        }

        await reconciliationRepository.SaveChangesAsync();
    }

    // ── Phase 1: Ledger internal consistency ─────────────────────────────

    private async Task<int> ReconcileLedgerInternalAsync(ReconciliationRun run, CancellationToken ct)
    {
        logger.LogInformation("[RECON:{RunId}] Fase 1: Verificacao interna do ledger", run.Id);

        var accounts = await ledgerRepository.GetAccountsWithEntryTotalsAsync(run.TenantId);

        foreach (var account in accounts)
        {
            if (ct.IsCancellationRequested) break;

            if (account.CurrentBalance != account.SumOfEntries)
            {
                long expectedCents = (long)(account.SumOfEntries * 100);
                long actualCents = (long)(account.CurrentBalance * 100);

                run.AddIssue(
                    ReconciliationIssueType.LEDGER_BALANCE_MISMATCH, "CRITICAL",
                    internalId: account.AccountId.ToString(), externalId: null,
                    expectedCents, actualCents,
                    $"Account {account.Type} (Seller: {account.SellerId}) balance ({account.CurrentBalance}) != entries sum ({account.SumOfEntries})");

                logger.LogCritical(
                    "[RECON:{RunId}] LEDGER_BALANCE_MISMATCH: Account {AccountId} balance {Balance} != entries {Sum}",
                    run.Id, account.AccountId, account.CurrentBalance, account.SumOfEntries);
            }
        }

        // Global double-entry invariant: sum of ALL entries across the tenant must be zero
        decimal globalSum = accounts.Sum(a => a.SumOfEntries);
        if (Math.Abs(globalSum) > 0.001m)
        {
            long globalSumCents = (long)(globalSum * 100);
            run.AddIssue(
                ReconciliationIssueType.LEDGER_GLOBAL_IMBALANCE, "CRITICAL",
                internalId: run.TenantId.ToString(), externalId: null,
                expectedCents: 0, actualCents: globalSumCents,
                $"Global double-entry imbalance: sum of all entries = {globalSum:F2} (expected 0). Tenant {run.TenantId}");

            logger.LogCritical(
                "[RECON:{RunId}] LEDGER_GLOBAL_IMBALANCE: Tenant {TenantId} sum = {Sum}",
                run.Id, run.TenantId, globalSum);
        }

        int checked_ = accounts.Count;
        logger.LogInformation("[RECON:{RunId}] Ledger: {Count} contas verificadas", run.Id, checked_);
        return checked_;
    }

    // ── Phase 2: Transaction-level 1:1 reconciliation ────────────────────

    private async Task<int> ReconcileTransactionsAsync(
        ReconciliationRun run, Guid tenantId, string apiKey,
        DateTime periodStart, DateTime periodEnd, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId);
        var chargeMode = tenant?.Config?.StripeChargeMode ?? StripeChargeMode.DESTINATION_CHARGE;

        logger.LogInformation("[RECON:{RunId}] Fase 2: Reconciliacao transacao-a-transacao (charge_mode={ChargeMode})",
            run.Id, chargeMode);

        // Get Stripe charges for the period
        // Note: For DirectCharge, platform-level charges won't include connected account charges.
        // Event-driven recon (ReconcileTransactionAsync) handles DirectCharge individually.
        long sinceUnix = new DateTimeOffset(periodStart).ToUnixTimeSeconds();
        long untilUnix = new DateTimeOffset(periodEnd).ToUnixTimeSeconds();

        var stripeCharges = await stripeApiClient.ListChargesAsync(apiKey, createdGte: sinceUnix, createdLte: untilUnix);
        var stripeChargesByPI = stripeCharges.Data
            .Where(c => !string.IsNullOrEmpty(c.PaymentIntent))
            .ToDictionary(c => c.PaymentIntent!, c => c);

        // Get internal Stripe transactions for the period
        var internalTxs = await transactionRepository.GetByTenantAndDateRangeAsync(
            tenantId, periodStart, periodEnd, provider: PaymentProvider.STRIPE);

        var processedPIs = new HashSet<string>();

        // Forward check: each internal transaction must exist in Stripe
        foreach (var tx in internalTxs)
        {
            if (ct.IsCancellationRequested) break;

            // Only check transactions that should have a Stripe object
            if (string.IsNullOrEmpty(tx.ProviderTxId)) continue;
            if (tx.Status == TransactionStatus.CREATED) continue;

            processedPIs.Add(tx.ProviderTxId);

            if (!stripeChargesByPI.TryGetValue(tx.ProviderTxId, out var charge))
            {
                // May be outside the charge list window — only flag CAPTURED transactions as critical
                if (tx.Status == TransactionStatus.CAPTURED || tx.Status == TransactionStatus.REFUNDED)
                {
                    run.AddIssue(
                        ReconciliationIssueType.MISSING_IN_STRIPE, "WARNING",
                        tx.Id.ToString(), tx.ProviderTxId,
                        (long)(tx.Amount * 100), null,
                        $"TX {tx.Id} (PI: {tx.ProviderTxId}) status {tx.Status} nao encontrada nos charges Stripe do periodo");

                    logger.LogWarning("[RECON:{RunId}] MISSING_IN_STRIPE: TX {TxId} PI {PI}",
                        run.Id, tx.Id, tx.ProviderTxId);
                }
                continue;
            }

            // Amount validation
            long expectedCents = (long)(tx.Amount * 100);
            if (charge.Amount != expectedCents)
            {
                run.AddIssue(
                    ReconciliationIssueType.AMOUNT_MISMATCH, "CRITICAL",
                    tx.Id.ToString(), charge.Id,
                    expectedCents, charge.Amount,
                    $"TX {tx.Id}: internal {expectedCents} cents != Stripe {charge.Amount} cents");

                logger.LogCritical("[RECON:{RunId}] AMOUNT_MISMATCH: TX {TxId} internal={Expected} stripe={Actual}",
                    run.Id, tx.Id, expectedCents, charge.Amount);
            }

            // Currency validation
            if (!string.Equals(tx.Currency, charge.Currency, StringComparison.OrdinalIgnoreCase))
            {
                run.AddIssue(
                    ReconciliationIssueType.CURRENCY_MISMATCH, "CRITICAL",
                    tx.Id.ToString(), charge.Id,
                    null, null,
                    $"TX {tx.Id}: internal currency '{tx.Currency}' != Stripe '{charge.Currency}'");
            }

            // Status validation
            ValidateStatusConsistency(run, tx, charge);

            // Refund validation
            ValidateRefundConsistency(run, tx, charge);
        }

        // Reverse check: each Stripe charge must have an internal transaction
        foreach (var charge in stripeCharges.Data)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(charge.PaymentIntent)) continue;
            if (processedPIs.Contains(charge.PaymentIntent)) continue;

            // This Stripe charge has no matching internal transaction
            run.AddIssue(
                ReconciliationIssueType.MISSING_IN_LEDGER, "CRITICAL",
                null, charge.Id,
                null, charge.Amount,
                $"Stripe charge {charge.Id} (PI: {charge.PaymentIntent}, {charge.Amount} cents) sem transacao interna");

            logger.LogCritical("[RECON:{RunId}] MISSING_IN_LEDGER: Charge {ChargeId} PI {PI} amount {Amount}",
                run.Id, charge.Id, charge.PaymentIntent, charge.Amount);
        }

        int totalChecked = internalTxs.Count(t => !string.IsNullOrEmpty(t.ProviderTxId) && t.Status != TransactionStatus.CREATED);
        logger.LogInformation("[RECON:{RunId}] Transacoes: {Count} verificadas", run.Id, totalChecked);
        return totalChecked;
    }

    private void ValidateStatusConsistency(ReconciliationRun run, Transaction tx, StripeChargeItem charge)
    {
        bool isMismatch = false;
        string detail = "";

        switch (charge.Status)
        {
            case "succeeded" when tx.Status is not (TransactionStatus.CAPTURED or TransactionStatus.REFUNDED or TransactionStatus.CHARGEBACKERROR):
                isMismatch = true;
                detail = $"Stripe succeeded but internal is {tx.Status}";
                break;

            case "failed" when tx.Status is not (TransactionStatus.DECLINED or TransactionStatus.FAILED):
                isMismatch = true;
                detail = $"Stripe failed but internal is {tx.Status}";
                break;
        }

        if (isMismatch)
        {
            run.AddIssue(
                ReconciliationIssueType.STATUS_MISMATCH, "WARNING",
                tx.Id.ToString(), charge.Id,
                null, null,
                $"TX {tx.Id}: {detail}");

            logger.LogWarning("[RECON:{RunId}] STATUS_MISMATCH: TX {TxId} — {Detail}",
                run.Id, tx.Id, detail);
        }
    }

    private void ValidateRefundConsistency(ReconciliationRun run, Transaction tx, StripeChargeItem charge)
    {
        if (charge.AmountRefunded == 0 && tx.RefundedAmount == 0) return;

        long internalRefundedCents = (long)(tx.RefundedAmount * 100);
        long diff = Math.Abs(charge.AmountRefunded - internalRefundedCents);

        if (diff > RoundingPolicy.ToleranceCents)
        {
            run.AddIssue(
                ReconciliationIssueType.REFUND_MISMATCH, "WARNING",
                tx.Id.ToString(), charge.Id,
                internalRefundedCents, charge.AmountRefunded,
                $"TX {tx.Id}: internal refunded {internalRefundedCents} cents != Stripe {charge.AmountRefunded} cents");

            logger.LogWarning("[RECON:{RunId}] REFUND_MISMATCH: TX {TxId} internal={Internal} stripe={Stripe}",
                run.Id, tx.Id, internalRefundedCents, charge.AmountRefunded);
        }
    }

    // ── Phase 3: Payout reconciliation ───────────────────────────────────

    private async Task<int> ReconcilePayoutsAsync(
        ReconciliationRun run, Guid tenantId, string? apiKey,
        DateTime periodStart, DateTime periodEnd, CancellationToken ct)
    {
        logger.LogInformation("[RECON:{RunId}] Fase 3: Reconciliacao de payouts", run.Id);

        var internalPayouts = await payoutRepository.GetByTenantAndDateRangeAsync(tenantId, periodStart, periodEnd);

        // Check each PAID payout has a corresponding ledger debit
        foreach (var payout in internalPayouts)
        {
            if (ct.IsCancellationRequested) break;

            if (payout.Status == PayoutStatus.PAID)
            {
                // Verify ledger has a PAYOUT debit for this specific payout
                var sellerAccounts = await ledgerRepository.GetAccountsBySellerAsync(tenantId, payout.SellerId);

                decimal payoutDebit = 0;
                foreach (var account in sellerAccounts)
                {
                    payoutDebit += account.Entries
                        .Where(e => e.ReferenceType == "PAYOUT" && e.Amount < 0 && e.ReferenceId == payout.Id.ToString())
                        .Sum(e => Math.Abs(e.Amount));
                }

                // If this specific payout has no corresponding ledger debit, flag it
                if (payoutDebit == 0)
                {
                    run.AddIssue(
                        ReconciliationIssueType.PAYOUT_MISSING_IN_LEDGER, "CRITICAL",
                        payout.Id.ToString(), payout.BankTransactionId,
                        (long)(payout.Amount * 100), 0,
                        $"Payout {payout.Id} (PAID, R${payout.Amount}) sem debit no ledger");

                    logger.LogCritical("[RECON:{RunId}] PAYOUT_MISSING_IN_LEDGER: {PayoutId}", run.Id, payout.Id);
                }
            }
        }

        // If Stripe is configured, verify transfers match
        if (!string.IsNullOrEmpty(apiKey))
        {
            await ReconcileStripeTransfersAsync(run, tenantId, apiKey, periodStart, periodEnd, internalPayouts, ct);
        }

        logger.LogInformation("[RECON:{RunId}] Payouts: {Count} verificados", run.Id, internalPayouts.Count);
        return internalPayouts.Count;
    }

    private async Task ReconcileStripeTransfersAsync(
        ReconciliationRun run, Guid tenantId, string apiKey,
        DateTime periodStart, DateTime periodEnd,
        List<Payout> internalPayouts, CancellationToken ct)
    {
        long sinceUnix = new DateTimeOffset(periodStart).ToUnixTimeSeconds();
        long untilUnix = new DateTimeOffset(periodEnd).ToUnixTimeSeconds();

        var stripeTransfers = await stripeApiClient.ListTransfersAsync(apiKey, createdGte: sinceUnix, createdLte: untilUnix);

        // Build lookup by metadata.payout_id or by amount+destination matching
        var transfersByPayoutId = new Dictionary<string, StripeTransferItem>();
        foreach (var transfer in stripeTransfers.Data)
        {
            if (transfer.Metadata?.TryGetValue("payout_id", out var payoutId) == true)
                transfersByPayoutId[payoutId] = transfer;
        }

        foreach (var payout in internalPayouts)
        {
            if (ct.IsCancellationRequested) break;
            if (payout.Status != PayoutStatus.PAID) continue;

            // Try to find matching transfer
            if (!transfersByPayoutId.TryGetValue(payout.Id.ToString(), out var transfer))
            {
                // Not necessarily an issue if payout uses OpenPix, not Stripe
                if (payout.BankProvider == PaymentProvider.STRIPE)
                {
                    run.AddIssue(
                        ReconciliationIssueType.PAYOUT_MISSING_IN_STRIPE, "CRITICAL",
                        payout.Id.ToString(), null,
                        (long)(payout.Amount * 100), null,
                        $"Stripe payout {payout.Id} (R${payout.Amount}) nao encontrado nos transfers Stripe");

                    logger.LogCritical("[RECON:{RunId}] PAYOUT_MISSING_IN_STRIPE: {PayoutId}", run.Id, payout.Id);
                }
                continue;
            }

            // Amount validation
            long expectedCents = (long)((payout.Amount - payout.Fee) * 100);
            if (Math.Abs(transfer.Amount - expectedCents) > RoundingPolicy.ToleranceCents)
            {
                run.AddIssue(
                    ReconciliationIssueType.PAYOUT_AMOUNT_MISMATCH, "CRITICAL",
                    payout.Id.ToString(), transfer.Id,
                    expectedCents, transfer.Amount,
                    $"Payout {payout.Id}: internal net {expectedCents} cents != Stripe transfer {transfer.Amount} cents");
            }
        }
    }

    // ── Phase 4: Platform balance drift ──────────────────────────────────

    private async Task ReconcilePlatformBalanceAsync(
        ReconciliationRun run, Guid tenantId, string apiKey, CancellationToken ct)
    {
        logger.LogInformation("[RECON:{RunId}] Fase 4: Verificacao de saldo da plataforma", run.Id);

        var stripeBalance = await stripeApiClient.GetBalanceAsync(apiKey);

        long availableCents = stripeBalance.Available?
            .Where(a => a.Currency == "brl")
            .Sum(a => a.Amount) ?? 0;

        long pendingCents = stripeBalance.Pending?
            .Where(a => a.Currency == "brl")
            .Sum(a => a.Amount) ?? 0;

        long totalStripeCents = availableCents + pendingCents;

        var platformReceivable = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_RECEIVABLE);
        var platformPayout = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_PAYOUT);
        var platformFee = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_FEE);
        var platformMargin = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_MARGIN);
        var providerCost = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PROVIDER_COST);
        var splitClearing = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.SPLIT_CLEARING);

        // PLATFORM_FEE is decomposed into PROVIDER_COST + PLATFORM_MARGIN via RecordPlatformMarginAsync.
        // SPLIT_CLEARING holds funds in transit between capture and distribution.
        // All must be included for a complete picture of platform funds.
        decimal internalTotal = (platformReceivable?.Balance ?? 0)
            - (platformPayout?.Balance ?? 0)
            + (platformFee?.Balance ?? 0)
            + (platformMargin?.Balance ?? 0)
            + (providerCost?.Balance ?? 0)
            + (splitClearing?.Balance ?? 0);

        long internalCents = (long)(internalTotal * 100);
        long drift = Math.Abs(totalStripeCents - internalCents);

        logger.LogInformation(
            "[RECON:{RunId}] Platform balance — Stripe: {StripeCents} (avail: {Avail}, pending: {Pending}), Internal: {InternalCents}, Drift: {Drift}",
            run.Id, totalStripeCents, availableCents, pendingCents, internalCents, drift);

        if (drift > 100) // > R$1.00
        {
            run.AddIssue(
                ReconciliationIssueType.PLATFORM_BALANCE_DRIFT,
                drift > 10000 ? "CRITICAL" : "WARNING",
                null, null,
                internalCents, totalStripeCents,
                $"Platform drift: Stripe {totalStripeCents} cents vs internal {internalCents} cents (drift: {drift})");
        }
    }

    // ── Event-driven: single transaction reconciliation ──────────────────

    public async Task ReconcileTransactionAsync(Guid tenantId, Guid transactionId, CancellationToken ct = default)
    {
        string? apiKey = configuration["Stripe:SecretKey"];
        if (string.IsNullOrEmpty(apiKey)) return;

        var tx = await transactionRepository.GetByIdAsync(tenantId, transactionId);
        if (tx == null || string.IsNullOrEmpty(tx.ProviderTxId)) return;
        if (tx.Provider != PaymentProvider.STRIPE) return;

        // Resolve charge mode and connected account for DirectCharge reconciliation
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId);
        var chargeMode = tenant?.Config?.StripeChargeMode ?? StripeChargeMode.DESTINATION_CHARGE;
        string? connectedAccountId = null;
        if (chargeMode == StripeChargeMode.DIRECT_CHARGE && tx.SellerId.HasValue)
        {
            var seller = await sellerRepository.GetByIdAsync(tenantId, tx.SellerId.Value);
            connectedAccountId = seller?.ExternalAccountId;
        }

        var run = ReconciliationRun.Create(tenantId, "EVENT_TX", tx.CreatedAt, DateTime.UtcNow);
        reconciliationRepository.AddRun(run);

        logger.LogInformation("[RECON:{RunId}] TX {TxId} charge_mode={ChargeMode} connected_account={Account}",
            run.Id, tx.Id, chargeMode, connectedAccountId ?? "platform");

        try
        {
            // Fetch the specific PI (use Stripe-Account for DirectCharge)
            StripePaymentIntentDetailResponse? pi = null;
            try
            {
                pi = await stripeApiClient.GetPaymentIntentAsync(apiKey, tx.ProviderTxId, connectedAccountId);
            }
            catch
            {
                run.AddIssue(
                    ReconciliationIssueType.MISSING_IN_STRIPE, "CRITICAL",
                    tx.Id.ToString(), tx.ProviderTxId,
                    (long)(tx.Amount * 100), null,
                    $"PaymentIntent {tx.ProviderTxId} nao encontrado no Stripe");
                run.Complete(1, 0, 0, 1, 1, 0);
                await reconciliationRepository.SaveChangesAsync();
                return;
            }

            // Amount validation
            long expectedCents = (long)(tx.Amount * 100);
            if (pi.Amount != expectedCents)
            {
                run.AddIssue(
                    ReconciliationIssueType.AMOUNT_MISMATCH, "CRITICAL",
                    tx.Id.ToString(), pi.Id,
                    expectedCents, pi.Amount,
                    $"TX {tx.Id}: internal {expectedCents} cents != PI amount {pi.Amount} cents");
            }

            // Currency validation
            if (!string.Equals(tx.Currency, pi.Currency, StringComparison.OrdinalIgnoreCase))
            {
                run.AddIssue(
                    ReconciliationIssueType.CURRENCY_MISMATCH, "CRITICAL",
                    tx.Id.ToString(), pi.Id,
                    null, null,
                    $"TX {tx.Id}: internal '{tx.Currency}' != Stripe '{pi.Currency}'");
            }

            // Status validation
            bool statusOk = pi.Status switch
            {
                "succeeded" => tx.Status is TransactionStatus.CAPTURED or TransactionStatus.REFUNDED or TransactionStatus.CHARGEBACKERROR,
                "canceled" => tx.Status is TransactionStatus.VOIDED or TransactionStatus.FAILED,
                "requires_payment_method" => tx.Status is TransactionStatus.DECLINED or TransactionStatus.FAILED or TransactionStatus.CREATED,
                _ => true
            };

            if (!statusOk)
            {
                run.AddIssue(
                    ReconciliationIssueType.STATUS_MISMATCH, "WARNING",
                    tx.Id.ToString(), pi.Id,
                    null, null,
                    $"TX {tx.Id}: Stripe PI status '{pi.Status}' vs internal '{tx.Status}'");
            }

            int issues = run.Issues.Count;
            run.Complete(1, 0, 0, issues, run.Issues.Count(i => i.Severity == "CRITICAL"), 0);

            if (issues > 0)
                logger.LogWarning("[RECON:{RunId}] Event TX recon: {Issues} issues for TX {TxId}", run.Id, issues, tx.Id);
        }
        catch (Exception ex)
        {
            run.Fail(ex.Message);
            logger.LogError(ex, "[RECON:{RunId}] Erro na reconciliacao event TX {TxId}", run.Id, tx.Id);
        }

        await reconciliationRepository.SaveChangesAsync();
    }

    // ── Event-driven: single payout reconciliation ───────────────────────

    public async Task ReconcilePayoutAsync(Guid tenantId, Guid payoutId, CancellationToken ct = default)
    {
        var payout = await payoutRepository.GetByIdAsync(tenantId, payoutId);
        if (payout == null) return;

        var run = ReconciliationRun.Create(tenantId, "EVENT_PAYOUT", payout.CreatedAt, DateTime.UtcNow);
        reconciliationRepository.AddRun(run);

        try
        {
            if (payout.Status == PayoutStatus.PAID)
            {
                // Verify ledger debit exists for THIS specific payout
                var sellerAccounts = await ledgerRepository.GetAccountsBySellerAsync(tenantId, payout.SellerId);
                bool hasPayoutDebit = sellerAccounts.Any(a =>
                    a.Entries.Any(e => e.ReferenceType == "PAYOUT" && e.Amount < 0
                                      && e.ReferenceId == payout.Id.ToString()));

                if (!hasPayoutDebit)
                {
                    run.AddIssue(
                        ReconciliationIssueType.PAYOUT_MISSING_IN_LEDGER, "CRITICAL",
                        payout.Id.ToString(), payout.BankTransactionId,
                        (long)(payout.Amount * 100), 0,
                        $"Payout PAID {payout.Id} sem debit no ledger");
                }
            }

            int issues = run.Issues.Count;
            run.Complete(0, 1, 0, issues, run.Issues.Count(i => i.Severity == "CRITICAL"), 0);

            if (issues > 0)
                logger.LogWarning("[RECON:{RunId}] Event payout recon: {Issues} issues for payout {PayoutId}",
                    run.Id, issues, payout.Id);
        }
        catch (Exception ex)
        {
            run.Fail(ex.Message);
            logger.LogError(ex, "[RECON:{RunId}] Erro na reconciliacao event payout {PayoutId}", run.Id, payout.Id);
        }

        await reconciliationRepository.SaveChangesAsync();
    }

    // ── Settlement cost reconciliation ───────────────────────────────────

    public async Task ApplyActualProviderCostAsync(Guid tenantId, Guid transactionId, decimal actualCost, CancellationToken ct = default)
    {
        var tx = await transactionRepository.GetByIdAsync(tenantId, transactionId);
        if (tx == null)
        {
            logger.LogWarning("[RECON] Transaction {TxId} not found for cost reconciliation", transactionId);
            return;
        }

        if (tx.Status != TransactionStatus.CAPTURED)
        {
            logger.LogWarning("[RECON] Transaction {TxId} not CAPTURED (status: {Status}), skipping cost reconciliation", transactionId, tx.Status);
            return;
        }

        decimal estimatedCost = tx.ProviderCostAmount ?? 0m;
        decimal adjustment = actualCost - estimatedCost;

        // Set actual cost on the transaction
        await transactionRepository.SetActualProviderCostAsync(transactionId, actualCost);

        // If difference exceeds tolerance (1 cent), create ledger adjustment
        if (Math.Abs(adjustment) > 0.01m)
        {
            await ledgerService.RecordCostAdjustmentAsync(
                tenantId,
                adjustment,
                $"Cost adjustment TX {transactionId}: estimated R${estimatedCost} → actual R${actualCost}",
                transactionId.ToString());

            logger.LogInformation(
                "[RECON] Provider cost adjusted for TX {TxId}: estimated={Estimated}, actual={Actual}, adjustment={Adjustment}",
                transactionId, estimatedCost, actualCost, adjustment);
        }
    }

    // ── Phase 5: Cross-rail invariants (V2) ────────────────────────────

    private async Task ReconcileCrossRailInvariantsAsync(
        ReconciliationRun run, Guid tenantId,
        DateTime periodStart, DateTime periodEnd, CancellationToken ct)
    {
        logger.LogInformation("[RECON:{RunId}] Fase 5: Invariantes cross-rail", run.Id);

        var transactions = await transactionRepository.GetByTenantAndDateRangeAsync(
            tenantId, periodStart, periodEnd);

        // Check 1: No PaymentIntent should have multiple CAPTURED transactions
        var capturedByIntent = transactions
            .Where(t => t.PaymentIntentId.HasValue && t.Status == TransactionStatus.CAPTURED)
            .GroupBy(t => t.PaymentIntentId!.Value)
            .Where(g => g.Count() > 1);

        foreach (var group in capturedByIntent)
        {
            if (ct.IsCancellationRequested) break;
            var txIds = string.Join(", ", group.Select(t => t.Id));
            run.AddIssue(
                ReconciliationIssueType.DOUBLE_CAPTURE, "CRITICAL",
                group.Key.ToString(), txIds,
                null, null,
                $"PaymentIntent {group.Key} has {group.Count()} captured transactions: [{txIds}]");

            logger.LogCritical("[RECON:{RunId}] DOUBLE_CAPTURE: Intent {IntentId} captured by {TxIds}",
                run.Id, group.Key, txIds);
        }

        // Check 2: Every CHARGEBACKERROR transaction should have a Dispute entity
        var chargebacks = transactions.Where(t => t.Status == TransactionStatus.CHARGEBACKERROR);
        foreach (var tx in chargebacks)
        {
            if (ct.IsCancellationRequested) break;
            var dispute = await disputeRepository.GetByTransactionIdAsync(tx.Id);
            if (dispute == null)
            {
                run.AddIssue(
                    ReconciliationIssueType.DISPUTE_ORPHAN, "WARNING",
                    tx.Id.ToString(), null,
                    null, null,
                    $"Transaction {tx.Id} has CHARGEBACKERROR status but no Dispute entity");
            }
        }

        // Check 3: Refunded amount must match sum of completed RefundIntents
        var refundedTxs = transactions.Where(t => t.RefundedAmount > 0);
        foreach (var tx in refundedTxs)
        {
            if (ct.IsCancellationRequested) break;

            var intents = await refundIntentRepository.GetByTransactionIdAsync(tx.Id);
            if (intents.Count == 0) continue; // Pre-migration: no intents exist, skip

            decimal completedSum = intents
                .Where(r => r.Status == RefundIntentStatus.COMPLETED)
                .Sum(r => r.Amount);

            long refundedCents = (long)(tx.RefundedAmount * 100);
            long intentsCents = (long)(completedSum * 100);
            long diff = Math.Abs(refundedCents - intentsCents);

            // Allow 1 cent tolerance for rounding
            if (diff > 1)
            {
                run.AddIssue(
                    ReconciliationIssueType.REFUND_TOTAL_MISMATCH, "WARNING",
                    tx.Id.ToString(), null,
                    refundedCents, intentsCents,
                    $"TX {tx.Id}: RefundedAmount {refundedCents} cents != completed RefundIntents sum {intentsCents} cents (diff: {diff})");

                logger.LogWarning(
                    "[RECON:{RunId}] REFUND_TOTAL_MISMATCH: TX {TxId} refunded={Refunded} intents={Intents} diff={Diff}",
                    run.Id, tx.Id, refundedCents, intentsCents, diff);
            }
        }
        // Check 4: Provider cost estimated vs actual
        var capturedWithActualCost = transactions
            .Where(t => t.ProviderCostActualAmount.HasValue && t.ProviderCostAmount.HasValue);

        foreach (var tx in capturedWithActualCost)
        {
            if (ct.IsCancellationRequested) break;

            long estimatedCents = RoundingPolicy.ToCents(tx.ProviderCostAmount!.Value);
            long actualCents = RoundingPolicy.ToCents(tx.ProviderCostActualAmount!.Value);
            long diff = Math.Abs(estimatedCents - actualCents);

            if (diff > RoundingPolicy.ToleranceCents)
            {
                run.AddIssue(
                    ReconciliationIssueType.PROVIDER_COST_MISMATCH, "WARNING",
                    tx.Id.ToString(), null,
                    estimatedCents, actualCents,
                    $"TX {tx.Id}: estimated cost {estimatedCents} cents != actual cost {actualCents} cents (diff: {diff})");

                logger.LogWarning(
                    "[RECON:{RunId}] PROVIDER_COST_MISMATCH: TX {TxId} estimated={Estimated} actual={Actual} diff={Diff}",
                    run.Id, tx.Id, estimatedCents, actualCents, diff);
            }
        }
    }

    // ── Phase 6: Split integrity ────────────────────────────────────────

    private async Task ReconcileSplitsAsync(
        ReconciliationRun run, Guid tenantId,
        DateTime periodStart, DateTime periodEnd, CancellationToken ct)
    {
        logger.LogInformation("[RECON:{RunId}] Fase 6: Integridade de splits", run.Id);

        var transactions = await transactionRepository.GetByTenantAndDateRangeAsync(
            tenantId, periodStart, periodEnd);

        // For each CAPTURED transaction, load its SplitTransfers
        var captured = transactions.Where(t => t.Status == TransactionStatus.CAPTURED).ToList();

        foreach (var tx in captured)
        {
            if (ct.IsCancellationRequested) break;

            var transfers = await splitTransferRepository.GetByTransactionIdAsync(tenantId, tx.Id);
            if (transfers.Count == 0) continue;

            // Check 1: SPLIT_TOTAL_MISMATCH — sum of SplitTransfer amounts must not exceed transaction net
            decimal transferTotal = transfers
                .Where(t => t.Status != SplitTransferStatus.FAILED)
                .Sum(t => t.Amount);
            decimal expectedNet = tx.NetAmount ?? tx.Amount;

            long transferCents = RoundingPolicy.ToCents(transferTotal);
            long expectedCents = RoundingPolicy.ToCents(expectedNet);

            if (transferCents > expectedCents + RoundingPolicy.ToleranceCents)
            {
                long diff = transferCents - expectedCents;
                run.AddIssue(
                    ReconciliationIssueType.SPLIT_TOTAL_MISMATCH, "CRITICAL",
                    tx.Id.ToString(), null,
                    expectedCents, transferCents,
                    $"TX {tx.Id}: SplitTransfer total {transferCents} cents exceeds net {expectedCents} cents (over-credit: {diff})");

                logger.LogWarning(
                    "[RECON:{RunId}] SPLIT_TOTAL_MISMATCH: TX {TxId} transfers={TransferCents} net={NetCents}",
                    run.Id, tx.Id, transferCents, expectedCents);
            }

            // Check 2: SPLIT_DUPLICATE_CREDIT — same recipient+kind credited multiple times for same TX
            // Group by (RecipientSellerId, IsPrimaryShare) since a seller can legitimately have both
            // a recipient share AND a primary residual share
            var duplicateRecipients = transfers
                .Where(t => t.Status != SplitTransferStatus.FAILED)
                .GroupBy(t => new { t.RecipientSellerId, t.IsPrimaryShare })
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var dup in duplicateRecipients)
            {
                if (ct.IsCancellationRequested) break;
                run.AddIssue(
                    ReconciliationIssueType.SPLIT_DUPLICATE_CREDIT, "CRITICAL",
                    tx.Id.ToString(), dup.Key.RecipientSellerId.ToString(),
                    dup.Count(), null,
                    $"TX {tx.Id}: Recipient {dup.Key.RecipientSellerId} (primary={dup.Key.IsPrimaryShare}) has {dup.Count()} non-failed SplitTransfers (expected 1)");

                logger.LogCritical(
                    "[RECON:{RunId}] SPLIT_DUPLICATE_CREDIT: TX {TxId} recipient {SellerId} primary={IsPrimary} count={Count}",
                    run.Id, tx.Id, dup.Key.RecipientSellerId, dup.Key.IsPrimaryShare, dup.Count());
            }
        }

        // Check 3: SPLIT_REFUND_NOT_REVERSED — refunded transactions whose splits weren't reversed
        var refunded = transactions.Where(t => t.Status == TransactionStatus.REFUNDED).ToList();

        foreach (var tx in refunded)
        {
            if (ct.IsCancellationRequested) break;

            var transfers = await splitTransferRepository.GetByTransactionIdAsync(tenantId, tx.Id);
            if (transfers.Count == 0) continue;

            var nonReversed = transfers
                .Where(t => t.Status is SplitTransferStatus.RESERVED or SplitTransferStatus.PAID
                    or SplitTransferStatus.PARTIALLY_REVERSED)
                .Where(t => t.RemainingAmount > 0)
                .ToList();

            if (nonReversed.Count > 0)
            {
                run.AddIssue(
                    ReconciliationIssueType.SPLIT_REFUND_NOT_REVERSED, "CRITICAL",
                    tx.Id.ToString(), null,
                    nonReversed.Count, null,
                    $"TX {tx.Id}: REFUNDED but {nonReversed.Count} SplitTransfer(s) still active: {string.Join(", ", nonReversed.Select(t => $"{t.RecipientSellerId}:{t.Status}"))}");

                logger.LogCritical(
                    "[RECON:{RunId}] SPLIT_REFUND_NOT_REVERSED: TX {TxId} has {Count} non-reversed transfers",
                    run.Id, tx.Id, nonReversed.Count);
            }
        }

        // Check 4: SPLIT_CLEARING residual balance — should be 0 when all splits are distributed
        var clearingAccount = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.SPLIT_CLEARING);
        if (clearingAccount != null && clearingAccount.Balance != 0)
        {
            // Check if there are pending splits that justify a non-zero clearing balance
            var pendingSplitBatch = await transactionRepository.GetPendingSplitBatchAsync(1);
            bool hasPendingSplits = pendingSplitBatch.Any(p => p.TenantId == tenantId);

            if (!hasPendingSplits)
            {
                long clearingCents = RoundingPolicy.ToCents(clearingAccount.Balance);
                if (Math.Abs(clearingCents) > RoundingPolicy.ToleranceCents)
                {
                    run.AddIssue(
                        ReconciliationIssueType.SPLIT_CLEARING_NON_ZERO_NO_PENDING, "CRITICAL",
                        "SPLIT_CLEARING", null,
                        0, clearingCents,
                        $"SPLIT_CLEARING balance is {clearingAccount.Balance:F2} but no pending splits exist. Funds are stuck.");

                    logger.LogCritical(
                        "[RECON:{RunId}] SPLIT_CLEARING_NON_ZERO_NO_PENDING: balance={Balance} with no pending splits",
                        run.Id, clearingAccount.Balance);
                }
            }
        }
    }

    // ── Summary logging ──────────────────────────────────────────────────

    private void LogRunSummary(ReconciliationRun run)
    {
        if (run.IssuesFound == 0)
        {
            logger.LogInformation(
                "[RECON:{RunId}] PASSED — TX: {Tx}, Payouts: {Payouts}, Ledger: {Ledger}. Nenhuma discrepancia.",
                run.Id, run.TransactionsChecked, run.PayoutsChecked, run.LedgerAccountsChecked);
        }
        else
        {
            logger.LogCritical(
                "[RECON:{RunId}] FAILED — {Issues} issues ({Critical} critical). " +
                "TX: {Tx}, Payouts: {Payouts}, Ledger: {Ledger}, Drift: {Drift} cents",
                run.Id, run.IssuesFound, run.CriticalIssues,
                run.TransactionsChecked, run.PayoutsChecked, run.LedgerAccountsChecked,
                run.PlatformDriftCents);
        }
    }

    private async Task DispatchAlertsAsync(ReconciliationRun run, CancellationToken ct)
    {
        var critical = run.Issues.Where(i => i.Severity == "CRITICAL").ToList();
        var warnings = run.Issues.Where(i => i.Severity == "WARNING").ToList();

        foreach (var issue in critical)
        {
            try { await alertService.SendCriticalAlertAsync(issue, ct); }
            catch (Exception ex) { logger.LogError(ex, "[RECON:{RunId}] Falha ao enviar alerta critico", run.Id); }
        }

        if (warnings.Count > 0)
        {
            try { await alertService.SendWarningDigestAsync(warnings, ct); }
            catch (Exception ex) { logger.LogError(ex, "[RECON:{RunId}] Falha ao enviar digest de warnings", run.Id); }
        }
    }

    // ── Phase 8: Operational maturity (V5) ──────────────────────────────

    private async Task ReconcileOperationalChecksAsync(ReconciliationRun run, Guid tenantId, CancellationToken ct)
    {
        logger.LogInformation("[RECON:{RunId}] Fase 8: Verificacoes operacionais", run.Id);

        // Check 1: SELLER_WALLET_NEGATIVE — wallets podem ficar negativas
        // temporariamente quando um refund debita mais do que o saldo (líquido
        // + taxa do provider repassada). Esse é comportamento esperado, vai
        // ser coberto pelas próximas capturas/cash-in do seller. Por isso,
        // diferenciamos severidade: REFUND → WARNING, outras causas → CRITICAL.
        var negativeWallets = await ledgerRepository.GetNegativeWalletAccountsAsync(tenantId);
        foreach (var wallet in negativeWallets)
        {
            if (ct.IsCancellationRequested) break;
            long balanceCents = RoundingPolicy.ToCents(wallet.Balance);

            // Causa raiz: olhamos a última entrada (a que jogou no negativo).
            // Se for REFUND, downgrade pra WARNING — não é bug de ledger.
            var lastEntry = await ledgerRepository.GetLatestEntryAsync(wallet.Id);
            bool isRefundDriven = lastEntry?.ReferenceType == "REFUND";
            string severity = isRefundDriven ? "WARNING" : "CRITICAL";
            string causeNote = isRefundDriven ? " (causa: REFUND — esperado, será coberto por próximas capturas)" : "";

            run.AddIssue(
                ReconciliationIssueType.SELLER_WALLET_NEGATIVE, severity,
                wallet.Id.ToString(), wallet.SellerId?.ToString(),
                0, balanceCents,
                $"WALLET account {wallet.Id} (Seller: {wallet.SellerId}) has negative balance: R${wallet.Balance:F2}{causeNote}");

            if (isRefundDriven)
            {
                logger.LogWarning(
                    "[RECON:{RunId}] SELLER_WALLET_NEGATIVE (refund-driven): Account {AccountId} Seller {SellerId} balance={Balance}",
                    run.Id, wallet.Id, wallet.SellerId, wallet.Balance);
            }
            else
            {
                logger.LogCritical(
                    "[RECON:{RunId}] SELLER_WALLET_NEGATIVE: Account {AccountId} Seller {SellerId} balance={Balance}",
                    run.Id, wallet.Id, wallet.SellerId, wallet.Balance);
            }
        }

        // Check 2: LEDGER_ENTRY_DUPLICATE_IDEMPOTENCY — detect duplicate SPLIT_DISTRIBUTE entries
        int duplicates = await ledgerRepository.GetDuplicateIdempotencyKeyCountAsync(tenantId, "SPLIT_DISTRIBUTE");
        if (duplicates > 0)
        {
            run.AddIssue(
                ReconciliationIssueType.LEDGER_ENTRY_DUPLICATE_IDEMPOTENCY, "CRITICAL",
                $"count:{duplicates}", null,
                0, duplicates,
                $"{duplicates} SPLIT_DISTRIBUTE operation(s) have more than the expected 2 ledger entries (possible duplication)");

            logger.LogCritical(
                "[RECON:{RunId}] LEDGER_ENTRY_DUPLICATE_IDEMPOTENCY: {Count} duplicate reference keys in SPLIT_DISTRIBUTE",
                run.Id, duplicates);
        }

        // Check 3: REFUND_PROVIDER_SUCCESS_LEDGER_MISSING — completed refunds with no ledger entry
        var completedRefunds = await refundIntentRepository.GetCompletedByTenantAsync(
            tenantId, DateTime.UtcNow.AddDays(-7));

        foreach (var refund in completedRefunds)
        {
            if (ct.IsCancellationRequested) break;
            bool hasEntry = await ledgerRepository.HasEntryWithReferenceAsync(tenantId, "REFUND", refund.Id.ToString());
            if (!hasEntry)
            {
                run.AddIssue(
                    ReconciliationIssueType.REFUND_PROVIDER_SUCCESS_LEDGER_MISSING, "CRITICAL",
                    refund.Id.ToString(), refund.ProviderRefundId,
                    (long)(refund.Amount * 100), 0,
                    $"RefundIntent {refund.Id} is COMPLETED (provider: {refund.ProviderRefundId}) but has no REFUND ledger entry");

                logger.LogCritical(
                    "[RECON:{RunId}] REFUND_PROVIDER_SUCCESS_LEDGER_MISSING: RefundIntent {Id} amount={Amount}",
                    run.Id, refund.Id, refund.Amount);
            }
        }

        // Check 4: PLATFORM_MARGIN_NEGATIVE — margin account should not be significantly negative
        var marginAccount = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_MARGIN);
        if (marginAccount != null && marginAccount.Balance < -100m) // threshold: -R$100
        {
            long marginCents = RoundingPolicy.ToCents(marginAccount.Balance);
            run.AddIssue(
                ReconciliationIssueType.PLATFORM_MARGIN_NEGATIVE, "WARNING",
                marginAccount.Id.ToString(), null,
                0, marginCents,
                $"PLATFORM_MARGIN balance is R${marginAccount.Balance:F2} (significantly negative — platform is subsidizing provider costs)");

            logger.LogWarning(
                "[RECON:{RunId}] PLATFORM_MARGIN_NEGATIVE: balance={Balance}",
                run.Id, marginAccount.Balance);
        }

        // Check 5: DIRECT_CHARGE_WITH_SPLIT_LEGACY — transactions with DirectCharge + splits (should not happen)
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId);
        if (tenant?.Config?.StripeChargeMode == StripeChargeMode.DIRECT_CHARGE)
        {
            var recentTxs = await transactionRepository.GetByTenantAndDateRangeAsync(
                tenantId, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

            foreach (var tx in recentTxs.Where(t => t.Status == TransactionStatus.CAPTURED))
            {
                if (ct.IsCancellationRequested) break;
                var transfers = await splitTransferRepository.GetByTransactionIdAsync(tenantId, tx.Id);
                if (transfers.Count > 0)
                {
                    run.AddIssue(
                        ReconciliationIssueType.DIRECT_CHARGE_WITH_SPLIT_LEGACY, "CRITICAL",
                        tx.Id.ToString(), null,
                        null, null,
                        $"TX {tx.Id} uses DIRECT_CHARGE mode but has {transfers.Count} SplitTransfer(s). This is blocked by policy.");

                    logger.LogCritical(
                        "[RECON:{RunId}] DIRECT_CHARGE_WITH_SPLIT_LEGACY: TX {TxId} has {Count} splits",
                        run.Id, tx.Id, transfers.Count);
                }
            }
        }
    }

    // ── Phase 7: Stuck operations (V4) ──────────────────────────────────

    private async Task ReconcileStuckOperationsAsync(ReconciliationRun run, Guid tenantId, CancellationToken ct)
    {
        var stuckThreshold = TimeSpan.FromHours(1);

        // Stuck payouts (PROCESSING for > 1 hour without next retry)
        int stuckPayouts = await payoutRepository.GetStuckProcessingCountAsync(stuckThreshold);
        if (stuckPayouts > 0)
        {
            run.AddIssue(
                ReconciliationIssueType.PAYOUT_STUCK_PROCESSING, "CRITICAL",
                $"count:{stuckPayouts}", null,
                0, 0,
                $"{stuckPayouts} payout(s) stuck in PROCESSING for over {stuckThreshold.TotalMinutes} minutes");
        }

        // Stuck refunds (PROCESSING for > 1 hour without next retry)
        int stuckRefunds = await refundIntentRepository.GetStuckProcessingCountAsync(stuckThreshold);
        if (stuckRefunds > 0)
        {
            run.AddIssue(
                ReconciliationIssueType.REFUND_STUCK_PROCESSING, "CRITICAL",
                $"count:{stuckRefunds}", null,
                0, 0,
                $"{stuckRefunds} refund intent(s) stuck in PROCESSING for over {stuckThreshold.TotalMinutes} minutes");
        }

        // Stuck split transfers
        var (pendingSplits, failedSplits) = await splitTransferRepository.GetStatusCountsAsync(tenantId);
        // If we have split transfers RESERVED/PROCESSING for too long, flag them
        // (Using the existing GetStatusCountsAsync which already counts PENDING+RESERVED)
        if (pendingSplits > 10)
        {
            run.AddIssue(
                ReconciliationIssueType.SPLIT_TRANSFER_STUCK_PROCESSING, "WARNING",
                $"pending:{pendingSplits}", null,
                0, 0,
                $"{pendingSplits} split transfer(s) in PENDING/RESERVED state — may indicate processing delays");
        }
    }
}
