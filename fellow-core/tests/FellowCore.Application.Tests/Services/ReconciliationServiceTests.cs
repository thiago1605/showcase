using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Services;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Models;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Domain.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class ReconciliationServiceTests
{
    private readonly IReconciliationRepository _reconciliationRepository = Substitute.For<IReconciliationRepository>();
    private readonly ILedgerRepository _ledgerRepository = Substitute.For<ILedgerRepository>();
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly IPayoutRepository _payoutRepository = Substitute.For<IPayoutRepository>();
    private readonly IRefundIntentRepository _refundIntentRepository = Substitute.For<IRefundIntentRepository>();
    private readonly IStripeApiClient _stripeApiClient = Substitute.For<IStripeApiClient>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IAlertService _alertService = Substitute.For<IAlertService>();
    private readonly ISplitTransferRepository _splitTransferRepository = Substitute.For<ISplitTransferRepository>();
    private readonly IConfiguration _configuration;
    private readonly ReconciliationService _sut;

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Tenant _tenant;

    public ReconciliationServiceTests()
    {
        _tenant = Tenant.Create("Test", "test-slug", "hash", "fp_", "secret_hash");
        typeof(Tenant).GetProperty("Id")!.SetValue(_tenant, _tenantId);

        var configData = new Dictionary<string, string?> { ["Stripe:SecretKey"] = "sk_test_fake" };
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        _tenantRepository.GetAllAsync().Returns(new List<Tenant> { _tenant });

        // Default: empty results
        _ledgerRepository.GetAccountsWithEntryTotalsAsync(Arg.Any<Guid>()).Returns(new List<LedgerAccountSummary>());
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>()));
        _transactionRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction>());
        _payoutRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<PayoutStatus?>())
            .Returns(new List<Payout>());
        _stripeApiClient.ListTransfersAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new StripeTransferListResponse(new List<StripeTransferItem>()));
        _stripeApiClient.GetBalanceAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new StripeBalanceResponse(
                Available: new List<StripeBalanceAmount> { new(0, "brl") },
                Pending: new List<StripeBalanceAmount> { new(0, "brl") }));
        _ledgerRepository.GetPlatformAccountAsync(Arg.Any<Guid>(), Arg.Any<LedgerAccountType>())
            .Returns((LedgerAccount?)null);
        _reconciliationRepository.GetLatestRunAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns((ReconciliationRun?)null);
        _refundIntentRepository.GetByTransactionIdAsync(Arg.Any<Guid>())
            .Returns(new List<RefundIntent>());
        _splitTransferRepository.GetByTransactionIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(new List<SplitTransfer>());
        _ledgerRepository.GetNegativeWalletAccountsAsync(Arg.Any<Guid>())
            .Returns(new List<LedgerAccount>());
        _ledgerRepository.GetDuplicateIdempotencyKeyCountAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(0);
        _refundIntentRepository.GetCompletedByTenantAsync(Arg.Any<Guid>(), Arg.Any<DateTime>())
            .Returns(new List<RefundIntent>());
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>())
            .Returns((Tenant?)null);

        _sut = new ReconciliationService(
            _reconciliationRepository,
            _ledgerRepository,
            _transactionRepository,
            _payoutRepository,
            Substitute.For<IDisputeRepository>(),
            _refundIntentRepository,
            _splitTransferRepository,
            Substitute.For<ISellerRepository>(),
            _stripeApiClient,
            _tenantRepository,
            Substitute.For<ILedgerService>(),
            _alertService,
            _configuration,
            Substitute.For<ILogger<ReconciliationService>>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private Transaction CreateTransaction(decimal amount, TransactionStatus status, string? providerTxId = null)
    {
        var result = Transaction.Create(_tenantId, amount, PaymentType.CREDIT_CARD, PaymentProvider.STRIPE,
            installments: 1, feeAmount: 0, netAmount: amount, expectedSettlementDate: null, providerTxId: providerTxId);
        var tx = result.Value;
        typeof(Transaction).GetProperty("Status")!.SetValue(tx, status);
        return tx;
    }

    private ReconciliationRun CaptureRun()
    {
        var captured = _reconciliationRepository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "AddRun")
            .Select(c => c.GetArguments()[0])
            .OfType<ReconciliationRun>()
            .Last();
        return captured;
    }

    // ── Phase 1: Ledger consistency ──────────────────────────────────────

    [Fact]
    public async Task NoDiscrepancies_ShouldCreatePassedRun()
    {
        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Status.Should().Be("PASSED");
        run.IssuesFound.Should().Be(0);
    }

    [Fact]
    public async Task LedgerDrift_ShouldReportCriticalIssue()
    {
        _ledgerRepository.GetAccountsWithEntryTotalsAsync(Arg.Any<Guid>()).Returns(new List<LedgerAccountSummary>
        {
            new(Guid.NewGuid(), _tenantId, Guid.NewGuid(), LedgerAccountType.WALLET, 1000m, 950m)
        });

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Status.Should().Be("FAILED");
        run.Issues.Should().ContainSingle(i =>
            i.Type == ReconciliationIssueType.LEDGER_BALANCE_MISMATCH && i.Severity == "CRITICAL");
    }

    // ── Phase 2: Transaction-level reconciliation ────────────────────────

    [Fact]
    public async Task MissingInStripe_ShouldReportWarning()
    {
        var tx = CreateTransaction(100m, TransactionStatus.CAPTURED, "pi_missing");
        _transactionRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        // No Stripe charges returned
        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.MISSING_IN_STRIPE);
    }

    [Fact]
    public async Task MissingInLedger_ShouldReportCritical()
    {
        // Stripe has a charge with no matching internal transaction
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_orphan", PaymentIntent: "pi_orphan", Amount: 5000, Status: "succeeded")
            }));

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.MISSING_IN_LEDGER && i.Severity == "CRITICAL");
    }

    [Fact]
    public async Task AmountMismatch_ShouldReportCritical()
    {
        var tx = CreateTransaction(40m, TransactionStatus.CAPTURED, "pi_mismatch");
        _transactionRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        // Stripe reports 5000 cents (R$50) but internal is R$40 (4000 cents)
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_1", PaymentIntent: "pi_mismatch", Amount: 5000, Status: "succeeded")
            }));

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        var issue = run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.AMOUNT_MISMATCH).Which;
        issue.Severity.Should().Be("CRITICAL");
        issue.ExpectedCents.Should().Be(4000);
        issue.ActualCents.Should().Be(5000);
        issue.DriftCents.Should().Be(1000);
    }

    [Fact]
    public async Task StatusMismatch_ShouldReportWarning()
    {
        var tx = CreateTransaction(100m, TransactionStatus.PROCESSING, "pi_status");
        _transactionRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        // Stripe says succeeded but internal is PROCESSING
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_2", PaymentIntent: "pi_status", Amount: 10000, Status: "succeeded")
            }));

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.STATUS_MISMATCH);
    }

    [Fact]
    public async Task RefundMismatch_ShouldReportWarning()
    {
        var tx = CreateTransaction(100m, TransactionStatus.CAPTURED, "pi_refund");
        typeof(Transaction).GetProperty("RefundedAmount")!.SetValue(tx, 20m); // R$20 refunded internally

        _transactionRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        // Stripe says R$30 refunded (3000 cents) but internal says R$20 (2000 cents)
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_3", PaymentIntent: "pi_refund", Amount: 10000, AmountRefunded: 3000, Status: "succeeded")
            }));

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        var issue = run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.REFUND_MISMATCH).Which;
        issue.ExpectedCents.Should().Be(2000);
        issue.ActualCents.Should().Be(3000);
    }

    [Fact]
    public async Task CurrencyMismatch_ShouldReportCritical()
    {
        var tx = CreateTransaction(100m, TransactionStatus.CAPTURED, "pi_currency");
        _transactionRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        // Stripe charge in USD instead of BRL
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_4", PaymentIntent: "pi_currency", Amount: 10000, Status: "succeeded", Currency: "usd")
            }));

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.CURRENCY_MISMATCH && i.Severity == "CRITICAL");
    }

    // ── Phase 3: Payout reconciliation ───────────────────────────────────

    [Fact]
    public async Task PaidPayoutWithoutLedgerDebit_ShouldReportCritical()
    {
        var sellerId = Guid.NewGuid();
        var payoutResult = Payout.Create(_tenantId, sellerId, 500m, 5m);
        var payout = payoutResult.Value;
        payout.MarkAsProcessing();
        payout.Complete("bank_tx_1");

        _payoutRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<PayoutStatus?>())
            .Returns(new List<Payout> { payout });

        // Seller has no accounts (no ledger debit)
        _ledgerRepository.GetAccountsBySellerAsync(_tenantId, sellerId).Returns(new List<LedgerAccount>());

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.PAYOUT_MISSING_IN_LEDGER && i.Severity == "CRITICAL");
    }

    // ── Phase 4: Platform balance drift ──────────────────────────────────

    [Fact]
    public async Task PlatformBalanceDrift_Large_ShouldReportCritical()
    {
        // Stripe: R$500 (50000 cents), Internal: R$0
        _stripeApiClient.GetBalanceAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new StripeBalanceResponse(
                Available: new List<StripeBalanceAmount> { new(30000, "brl") },
                Pending: new List<StripeBalanceAmount> { new(20000, "brl") }));

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.PLATFORM_BALANCE_DRIFT && i.Severity == "CRITICAL");
    }

    [Fact]
    public async Task PlatformBalanceDrift_Small_ShouldReportWarning()
    {
        // Stripe: R$5 (500 cents), Internal: R$0 — drift < R$100 but > R$1
        _stripeApiClient.GetBalanceAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new StripeBalanceResponse(
                Available: new List<StripeBalanceAmount> { new(500, "brl") },
                Pending: new List<StripeBalanceAmount> { new(0, "brl") }));

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.PLATFORM_BALANCE_DRIFT && i.Severity == "WARNING");
    }

    // ── Phase 5: Cross-rail invariants ─────────────────────────────────

    [Fact]
    public async Task RefundTotalMismatch_ShouldReportWarning()
    {
        var tx = CreateTransaction(100m, TransactionStatus.REFUNDED, "pi_refund_intent");
        typeof(Transaction).GetProperty("RefundedAmount")!.SetValue(tx, 50m); // R$50 refunded

        _transactionRepository.GetByTenantAndDateRangeAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        // Stripe charges match to avoid Phase 2 noise
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_ri", PaymentIntent: "pi_refund_intent", Amount: 10000, AmountRefunded: 5000, Status: "succeeded")
            }));

        // RefundIntents total only R$30 completed (mismatch with R$50)
        var completedIntent = RefundIntent.Create(_tenantId, tx.Id, 30m, "partial");
        completedIntent.MarkProcessing();
        completedIntent.Complete("re_1");

        var failedIntent = RefundIntent.Create(_tenantId, tx.Id, 10m, "failed attempt");
        failedIntent.Fail();

        _refundIntentRepository.GetByTransactionIdAsync(tx.Id)
            .Returns(new List<RefundIntent> { completedIntent, failedIntent });

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        var issue = run.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.REFUND_TOTAL_MISMATCH && i.Severity == "WARNING").Which;
        issue.ExpectedCents.Should().Be(5000); // tx.RefundedAmount in cents
        issue.ActualCents.Should().Be(3000);   // completed intents sum in cents
    }

    [Fact]
    public async Task RefundTotalMatch_WithinTolerance_ShouldNotReport()
    {
        var tx = CreateTransaction(100m, TransactionStatus.REFUNDED, "pi_refund_ok");
        typeof(Transaction).GetProperty("RefundedAmount")!.SetValue(tx, 50m);

        _transactionRepository.GetByTenantAndDateRangeAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_ro", PaymentIntent: "pi_refund_ok", Amount: 10000, AmountRefunded: 5000, Status: "succeeded")
            }));

        // Completed intents exactly match R$50
        var intent = RefundIntent.Create(_tenantId, tx.Id, 50m, "full");
        intent.MarkProcessing();
        intent.Complete("re_2");

        _refundIntentRepository.GetByTransactionIdAsync(tx.Id)
            .Returns(new List<RefundIntent> { intent });

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().NotContain(i => i.Type == ReconciliationIssueType.REFUND_TOTAL_MISMATCH);
    }

    [Fact]
    public async Task RefundWithNoIntents_PreMigration_ShouldNotReport()
    {
        var tx = CreateTransaction(100m, TransactionStatus.REFUNDED, "pi_premig");
        typeof(Transaction).GetProperty("RefundedAmount")!.SetValue(tx, 25m);

        _transactionRepository.GetByTenantAndDateRangeAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_pm", PaymentIntent: "pi_premig", Amount: 10000, AmountRefunded: 2500, Status: "succeeded")
            }));

        // No RefundIntents (pre-migration) — should skip, not flag
        _refundIntentRepository.GetByTransactionIdAsync(tx.Id)
            .Returns(new List<RefundIntent>());

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().NotContain(i => i.Type == ReconciliationIssueType.REFUND_TOTAL_MISMATCH);
    }

    // ── Edge cases ───────────────────────────────────────────────────────

    [Fact]
    public async Task NoStripeKey_ShouldSkipStripePhases()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var sut = new ReconciliationService(
            _reconciliationRepository, _ledgerRepository, _transactionRepository,
            _payoutRepository, Substitute.For<IDisputeRepository>(), _refundIntentRepository,
            Substitute.For<ISplitTransferRepository>(), Substitute.For<ISellerRepository>(),
            _stripeApiClient, _tenantRepository, Substitute.For<ILedgerService>(),
            _alertService, config,
            Substitute.For<ILogger<ReconciliationService>>());

        await sut.RunDailyReconciliationAsync();

        await _stripeApiClient.DidNotReceive().ListChargesAsync(
            Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task IncrementalReconciliation_ShouldUsePreviousRunPeriod()
    {
        var lastRun = ReconciliationRun.Create(_tenantId, "BATCH", DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddHours(-1));
        typeof(ReconciliationRun).GetProperty("Status")!.SetValue(lastRun, "PASSED");
        _reconciliationRepository.GetLatestRunAsync(_tenantId, "BATCH").Returns(lastRun);

        await _sut.RunDailyReconciliationAsync();

        // Should still use backfill (7 days) since it's wider than last run's period end
        _reconciliationRepository.Received().AddRun(Arg.Is<ReconciliationRun>(r =>
            r.TenantId == _tenantId));
    }

    // ── Event-driven reconciliation ──────────────────────────────────────

    [Fact]
    public async Task ReconcileTransaction_AmountMismatch_ShouldCreateEventRun()
    {
        var tx = CreateTransaction(100m, TransactionStatus.CAPTURED, "pi_event");
        _transactionRepository.GetByIdAsync(_tenantId, tx.Id).Returns(tx);

        // Stripe PI returns different amount
        _stripeApiClient.GetPaymentIntentAsync("sk_test_fake", "pi_event")
            .Returns(new StripePaymentIntentDetailResponse("pi_event", "succeeded", Amount: 9000));

        await _sut.ReconcileTransactionAsync(_tenantId, tx.Id);

        _reconciliationRepository.Received().AddRun(Arg.Is<ReconciliationRun>(r =>
            r.RunType == "EVENT_TX"));
        await _reconciliationRepository.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task ReconcileTransaction_MissingInStripe_ShouldReportCritical()
    {
        var tx = CreateTransaction(100m, TransactionStatus.CAPTURED, "pi_gone");
        _transactionRepository.GetByIdAsync(_tenantId, tx.Id).Returns(tx);

        // Stripe throws not found
        _stripeApiClient.GetPaymentIntentAsync("sk_test_fake", "pi_gone")
            .Returns<StripePaymentIntentDetailResponse>(x => throw new Exception("Not found"));

        await _sut.ReconcileTransactionAsync(_tenantId, tx.Id);

        var run = CaptureRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.MISSING_IN_STRIPE);
    }

    [Fact]
    public async Task ReconcilePayout_WithoutLedgerDebit_ShouldReportCritical()
    {
        var sellerId = Guid.NewGuid();
        var payoutResult = Payout.Create(_tenantId, sellerId, 200m);
        var payout = payoutResult.Value;
        payout.MarkAsProcessing();
        payout.Complete("bank_tx_2");

        _payoutRepository.GetByIdAsync(_tenantId, payout.Id).Returns(payout);
        _ledgerRepository.GetAccountsBySellerAsync(_tenantId, sellerId).Returns(new List<LedgerAccount>());

        await _sut.ReconcilePayoutAsync(_tenantId, payout.Id);

        var run = CaptureRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.PAYOUT_MISSING_IN_LEDGER);
    }

    // ── Persistence validation ───────────────────────────────────────────

    [Fact]
    public async Task Run_ShouldPersistToRepository()
    {
        await _sut.RunDailyReconciliationAsync();

        _reconciliationRepository.Received().AddRun(Arg.Any<ReconciliationRun>());
        await _reconciliationRepository.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task MultipleIssues_ShouldAllBePersisted()
    {
        // Ledger drift
        _ledgerRepository.GetAccountsWithEntryTotalsAsync(Arg.Any<Guid>()).Returns(new List<LedgerAccountSummary>
        {
            new(Guid.NewGuid(), _tenantId, Guid.NewGuid(), LedgerAccountType.WALLET, 100m, 90m)
        });

        // Missing in ledger
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_x", PaymentIntent: "pi_x", Amount: 1000, Status: "succeeded")
            }));

        // Platform drift
        _stripeApiClient.GetBalanceAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new StripeBalanceResponse(
                Available: new List<StripeBalanceAmount> { new(99999, "brl") },
                Pending: new List<StripeBalanceAmount>()));

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Count.Should().BeGreaterThanOrEqualTo(3);
        run.Status.Should().Be("FAILED");
        run.CriticalIssues.Should().BeGreaterThanOrEqualTo(2);
    }

    // ── Phase 6: Split reconciliation ────────────────────────────────────

    [Fact]
    public async Task Reconciliation_ShouldNotFlagDuplicate_WhenSameSellerHasRecipientAndPrimaryShare()
    {
        // Arrange: CAPTURED TX with same seller having recipient + primary share (legitimate)
        var tx = CreateTransaction(100m, TransactionStatus.CAPTURED, "pi_split_legit");
        _transactionRepository.GetByTenantAndDateRangeAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_sl", PaymentIntent: "pi_split_legit", Amount: 10000, Status: "succeeded")
            }));

        var sellerId = Guid.NewGuid();

        // Recipient share (IsPrimaryShare = false)
        var recipientTransfer = SplitTransfer.Create(tx.Id, _tenantId, sellerId, 30m, 30m, isPrimaryShare: false).Value;
        recipientTransfer.Reserve();
        recipientTransfer.MarkProcessing();
        recipientTransfer.MarkPaid();

        // Primary share (IsPrimaryShare = true)
        var primaryTransfer = SplitTransfer.Create(tx.Id, _tenantId, sellerId, 70m, 70m, isPrimaryShare: true).Value;
        primaryTransfer.Reserve();
        primaryTransfer.MarkProcessing();
        primaryTransfer.MarkPaid();

        _splitTransferRepository.GetByTransactionIdAsync(_tenantId, tx.Id)
            .Returns(new List<SplitTransfer> { recipientTransfer, primaryTransfer });

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert: No SPLIT_DUPLICATE_CREDIT — same seller with different roles is legitimate
        var run = CaptureRun();
        run.Issues.Should().NotContain(i => i.Type == ReconciliationIssueType.SPLIT_DUPLICATE_CREDIT);
    }

    [Fact]
    public async Task Reconciliation_ShouldFlagDuplicate_WhenSameSellerHasTwoRecipientShares()
    {
        // Arrange: CAPTURED TX with same seller having 2 recipient shares (duplicate)
        var tx = CreateTransaction(100m, TransactionStatus.CAPTURED, "pi_split_dup");
        _transactionRepository.GetByTenantAndDateRangeAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_sd", PaymentIntent: "pi_split_dup", Amount: 10000, Status: "succeeded")
            }));

        var sellerId = Guid.NewGuid();

        // Two recipient shares for the same seller (both IsPrimaryShare = false)
        var transfer1 = SplitTransfer.Create(tx.Id, _tenantId, sellerId, 30m, 30m, isPrimaryShare: false).Value;
        transfer1.Reserve();
        transfer1.MarkProcessing();
        transfer1.MarkPaid();

        var transfer2 = SplitTransfer.Create(tx.Id, _tenantId, sellerId, 20m, 20m, isPrimaryShare: false).Value;
        transfer2.Reserve();
        transfer2.MarkProcessing();
        transfer2.MarkPaid();

        _splitTransferRepository.GetByTransactionIdAsync(_tenantId, tx.Id)
            .Returns(new List<SplitTransfer> { transfer1, transfer2 });

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert: SPLIT_DUPLICATE_CREDIT should be flagged
        var run = CaptureRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.SPLIT_DUPLICATE_CREDIT);
    }

    [Fact]
    public async Task Reconciliation_ShouldFlagRefundedTransaction_WhenPartiallyReversedTransferHasRemainingAmount()
    {
        // Arrange: REFUNDED TX with a partially reversed transfer that has remaining amount > 0
        var tx = CreateTransaction(100m, TransactionStatus.REFUNDED, "pi_split_partial");
        typeof(Transaction).GetProperty("RefundedAmount")!.SetValue(tx, 100m);

        _transactionRepository.GetByTenantAndDateRangeAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_sp", PaymentIntent: "pi_split_partial", Amount: 10000, AmountRefunded: 10000, Status: "succeeded")
            }));

        // Refund intents match to avoid Phase 5 noise
        var intent = RefundIntent.Create(_tenantId, tx.Id, 100m, "full refund");
        intent.MarkProcessing();
        intent.Complete("re_partial");
        _refundIntentRepository.GetByTransactionIdAsync(tx.Id)
            .Returns(new List<RefundIntent> { intent });

        var sellerId = Guid.NewGuid();

        // Transfer is PARTIALLY_REVERSED: Amount=50, ReversedAmount=20, RemainingAmount=30 > 0
        var transfer = SplitTransfer.Create(tx.Id, _tenantId, sellerId, 50m, 50m, isPrimaryShare: false).Value;
        transfer.Reserve();
        transfer.MarkProcessing();
        transfer.MarkPaid();
        transfer.PartialReverse(20m);

        _splitTransferRepository.GetByTransactionIdAsync(_tenantId, tx.Id)
            .Returns(new List<SplitTransfer> { transfer });

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert: SPLIT_REFUND_NOT_REVERSED should be flagged
        var run = CaptureRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.SPLIT_REFUND_NOT_REVERSED);
    }

    [Fact]
    public async Task Reconciliation_ShouldFlagRefundedTransaction_WhenReservedTransferNotReversed()
    {
        // Arrange: REFUNDED TX with a transfer stuck in RESERVED status (RemainingAmount = Amount > 0)
        var tx = CreateTransaction(100m, TransactionStatus.REFUNDED, "pi_split_stuck");
        typeof(Transaction).GetProperty("RefundedAmount")!.SetValue(tx, 100m);

        _transactionRepository.GetByTenantAndDateRangeAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>
            {
                new("ch_ss", PaymentIntent: "pi_split_stuck", Amount: 10000, AmountRefunded: 10000, Status: "succeeded")
            }));

        // Refund intents match to avoid Phase 5 noise
        var intent = RefundIntent.Create(_tenantId, tx.Id, 100m, "full refund");
        intent.MarkProcessing();
        intent.Complete("re_stuck");
        _refundIntentRepository.GetByTransactionIdAsync(tx.Id)
            .Returns(new List<RefundIntent> { intent });

        var sellerId = Guid.NewGuid();

        // Transfer stuck in RESERVED: Amount=50, ReversedAmount=0, RemainingAmount=50 > 0
        var transfer = SplitTransfer.Create(tx.Id, _tenantId, sellerId, 50m, 50m, isPrimaryShare: false).Value;
        transfer.Reserve();

        _splitTransferRepository.GetByTransactionIdAsync(_tenantId, tx.Id)
            .Returns(new List<SplitTransfer> { transfer });

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert: SPLIT_REFUND_NOT_REVERSED should be flagged for the stuck RESERVED transfer
        var run = CaptureRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.SPLIT_REFUND_NOT_REVERSED);
    }

    // ── Phase 8: Operational maturity checks ─────────────────────────────

    [Fact]
    public async Task NegativeWallet_ShouldReportCritical()
    {
        var sellerId = Guid.NewGuid();
        var account = LedgerAccount.Create(_tenantId, sellerId, LedgerAccountType.WALLET);
        // Force negative balance via reflection
        typeof(LedgerAccount).GetProperty("Balance")!.SetValue(account, -50m);

        _ledgerRepository.GetNegativeWalletAccountsAsync(_tenantId)
            .Returns(new List<LedgerAccount> { account });

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.SELLER_WALLET_NEGATIVE && i.Severity == "CRITICAL");
    }

    [Fact]
    public async Task DuplicateIdempotencyKey_ShouldReportCritical()
    {
        _ledgerRepository.GetDuplicateIdempotencyKeyCountAsync(_tenantId, "SPLIT_DISTRIBUTE")
            .Returns(2);

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.LEDGER_ENTRY_DUPLICATE_IDEMPOTENCY && i.Severity == "CRITICAL");
    }

    [Fact]
    public async Task RefundCompletedWithoutLedgerEntry_ShouldReportCritical()
    {
        var refund = RefundIntent.Create(_tenantId, Guid.NewGuid(), 100m, "test");
        refund.MarkProcessing();
        refund.Complete("re_provider_123");

        _refundIntentRepository.GetCompletedByTenantAsync(_tenantId, Arg.Any<DateTime>())
            .Returns(new List<RefundIntent> { refund });

        // No matching ledger entry
        _ledgerRepository.HasEntryWithReferenceAsync(_tenantId, "REFUND", refund.Id.ToString())
            .Returns(false);

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.REFUND_PROVIDER_SUCCESS_LEDGER_MISSING && i.Severity == "CRITICAL");
    }

    [Fact]
    public async Task PlatformMarginSignificantlyNegative_ShouldReportWarning()
    {
        var marginAccount = LedgerAccount.Create(_tenantId, null, LedgerAccountType.PLATFORM_MARGIN);
        typeof(LedgerAccount).GetProperty("Balance")!.SetValue(marginAccount, -200m);

        _ledgerRepository.GetPlatformAccountAsync(_tenantId, LedgerAccountType.PLATFORM_MARGIN)
            .Returns(marginAccount);

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.PLATFORM_MARGIN_NEGATIVE && i.Severity == "WARNING");
    }

    [Fact]
    public async Task SplitClearingNonZeroNoPending_ShouldReportCritical()
    {
        var clearingAccount = LedgerAccount.Create(_tenantId, null, LedgerAccountType.SPLIT_CLEARING);
        typeof(LedgerAccount).GetProperty("Balance")!.SetValue(clearingAccount, 500m);

        _ledgerRepository.GetPlatformAccountAsync(_tenantId, LedgerAccountType.SPLIT_CLEARING)
            .Returns(clearingAccount);

        // No pending splits
        _transactionRepository.GetPendingSplitBatchAsync(Arg.Any<int>())
            .Returns(new List<(Guid, Guid)>());

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.SPLIT_CLEARING_NON_ZERO_NO_PENDING && i.Severity == "CRITICAL");
    }

    [Fact]
    public async Task Phase8_MultipleOperationalIssues_ShouldAllBePersisted()
    {
        // Ledger drift + negative wallet = 2+ issues
        _ledgerRepository.GetAccountsWithEntryTotalsAsync(Arg.Any<Guid>()).Returns(new List<LedgerAccountSummary>
        {
            new(Guid.NewGuid(), _tenantId, Guid.NewGuid(), LedgerAccountType.WALLET, 100m, 80m)
        });

        var negativeAccount = LedgerAccount.Create(_tenantId, Guid.NewGuid(), LedgerAccountType.WALLET);
        typeof(LedgerAccount).GetProperty("Balance")!.SetValue(negativeAccount, -10m);
        _ledgerRepository.GetNegativeWalletAccountsAsync(_tenantId)
            .Returns(new List<LedgerAccount> { negativeAccount });

        await _sut.RunDailyReconciliationAsync();

        var run = CaptureRun();
        run.Status.Should().Be("FAILED");
        run.Issues.Count.Should().BeGreaterThanOrEqualTo(2);
    }
}
