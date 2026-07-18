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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Flows;

/// <summary>
/// T15: Reconciliation Phase 5 — Cross-rail invariant tests.
/// Tests DOUBLE_CAPTURE, DISPUTE_ORPHAN, REFUND_TOTAL_MISMATCH, and LEDGER_GLOBAL_IMBALANCE detection.
/// </summary>
public class ReconciliationPhase5Tests
{
    private readonly IReconciliationRepository _reconciliationRepository = Substitute.For<IReconciliationRepository>();
    private readonly ILedgerRepository _ledgerRepository = Substitute.For<ILedgerRepository>();
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly IPayoutRepository _payoutRepository = Substitute.For<IPayoutRepository>();
    private readonly IDisputeRepository _disputeRepository = Substitute.For<IDisputeRepository>();
    private readonly IRefundIntentRepository _refundIntentRepository = Substitute.For<IRefundIntentRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly IStripeApiClient _stripeApiClient = Substitute.For<IStripeApiClient>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IAlertService _alertService = Substitute.For<IAlertService>();
    private readonly IConfiguration _configuration;
    private readonly ReconciliationService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    public ReconciliationPhase5Tests()
    {
        var configValues = new Dictionary<string, string?>
        {
            { "Stripe:SecretKey", "sk_test_fake" }
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        _sut = new ReconciliationService(
            _reconciliationRepository, _ledgerRepository, _transactionRepository,
            _payoutRepository, _disputeRepository, _refundIntentRepository,
            Substitute.For<ISplitTransferRepository>(),
            _sellerRepository, _stripeApiClient, _tenantRepository,
            Substitute.For<ILedgerService>(),
            _alertService, _configuration,
            Substitute.For<ILogger<ReconciliationService>>());
    }

    // ── DOUBLE_CAPTURE Detection ───────────────────────────────────────

    [Fact]
    public async Task RunDailyReconciliation_ShouldDetectDoubleCaptureOnSamePaymentIntent()
    {
        // Arrange: two CAPTURED transactions sharing the same PaymentIntentId
        var intentId = Guid.NewGuid();
        var tx1 = BuildTransaction(TransactionStatus.CAPTURED, intentId);
        var tx2 = BuildTransaction(TransactionStatus.CAPTURED, intentId);

        var tenant = BuildTenant();
        _tenantRepository.GetAllAsync().Returns(new List<Tenant> { tenant });
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        // Phase 1: no ledger issues
        _ledgerRepository.GetAccountsWithEntryTotalsAsync(TenantId)
            .Returns(new List<LedgerAccountSummary>());

        // Phase 2: skip (no Stripe charges needed for Phase 5 test)
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>()));

        // Phase 3: no payouts + no transfers
        _payoutRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<PayoutStatus?>())
            .Returns(new List<Payout>());
        _stripeApiClient.ListTransfersAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new StripeTransferListResponse(new List<StripeTransferItem>()));

        // Phase 4: balanced
        _stripeApiClient.GetBalanceAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new StripeBalanceResponse(
                Available: new List<StripeBalanceAmount> { new(0, "brl") },
                Pending: new List<StripeBalanceAmount> { new(0, "brl") }));

        // Return transactions conditionally based on provider filter
        _transactionRepository.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(callInfo =>
            {
                var provider = callInfo.ArgAt<PaymentProvider?>(4);
                return provider.HasValue ? new List<Transaction>() : new List<Transaction> { tx1, tx2 };
            });

        // No disputes/refunds
        _disputeRepository.GetByTransactionIdAsync(Arg.Any<Guid>()).Returns((Dispute?)null);
        _refundIntentRepository.GetByTransactionIdAsync(Arg.Any<Guid>()).Returns(new List<RefundIntent>());

        _reconciliationRepository.GetLatestRunAsync(TenantId, "BATCH").Returns((ReconciliationRun?)null);

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert: a run was saved
        await _reconciliationRepository.Received(1).SaveChangesAsync();

        // Assert: verify the run has a DOUBLE_CAPTURE issue
        var capturedRun = CaptureReconciliationRun();
        capturedRun.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.DOUBLE_CAPTURE);
    }

    // ── DISPUTE_ORPHAN Detection ───────────────────────────────────────

    [Fact]
    public async Task RunDailyReconciliation_ShouldDetectDisputeOrphan()
    {
        // Arrange: transaction with CHARGEBACKERROR but no Dispute entity
        var tx = BuildTransaction(TransactionStatus.CHARGEBACKERROR);

        var tenant = BuildTenant();
        SetupMinimalTenant(tenant, new List<Transaction> { tx });

        // No matching dispute
        _disputeRepository.GetByTransactionIdAsync(Arg.Any<Guid>()).Returns((Dispute?)null);
        _refundIntentRepository.GetByTransactionIdAsync(Arg.Any<Guid>()).Returns(new List<RefundIntent>());

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert
        var run = CaptureReconciliationRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.DISPUTE_ORPHAN);
    }

    [Fact]
    public async Task RunDailyReconciliation_ShouldNotFlagDisputeOrphan_WhenDisputeExists()
    {
        // Arrange
        var txId = Guid.NewGuid();
        var tx = BuildTransaction(TransactionStatus.CHARGEBACKERROR, txId: txId);
        var dispute = Dispute.Create(TenantId, txId, SellerId, "dp_001", 95m);

        var tenant = BuildTenant();
        SetupMinimalTenant(tenant, new List<Transaction> { tx });

        _disputeRepository.GetByTransactionIdAsync(txId).Returns(dispute);
        _refundIntentRepository.GetByTransactionIdAsync(Arg.Any<Guid>()).Returns(new List<RefundIntent>());

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert: no orphan
        var run = CaptureReconciliationRun();
        run.Issues.Should().NotContain(i => i.Type == ReconciliationIssueType.DISPUTE_ORPHAN);
    }

    // ── REFUND_TOTAL_MISMATCH Detection ────────────────────────────────

    [Fact]
    public async Task RunDailyReconciliation_ShouldDetectRefundTotalMismatch()
    {
        // Arrange: transaction with RefundedAmount = 50 but completed intents sum = 30
        var txId = Guid.NewGuid();
        var tx = BuildTransaction(TransactionStatus.CAPTURED, txId: txId, refundedAmount: 50m);

        var intent1 = RefundIntent.Create(TenantId, txId, 20m);
        intent1.Complete("re_001");
        var intent2 = RefundIntent.Create(TenantId, txId, 10m);
        intent2.Complete("re_002");

        var tenant = BuildTenant();
        SetupMinimalTenant(tenant, new List<Transaction> { tx });

        _disputeRepository.GetByTransactionIdAsync(Arg.Any<Guid>()).Returns((Dispute?)null);
        _refundIntentRepository.GetByTransactionIdAsync(txId).Returns(new List<RefundIntent> { intent1, intent2 });

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert
        var run = CaptureReconciliationRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.REFUND_TOTAL_MISMATCH);
    }

    [Fact]
    public async Task RunDailyReconciliation_ShouldNotFlagRefundMismatch_WhenWithinTolerance()
    {
        // Arrange: 1 cent difference (within tolerance)
        var txId = Guid.NewGuid();
        var tx = BuildTransaction(TransactionStatus.CAPTURED, txId: txId, refundedAmount: 50.01m);

        var intent = RefundIntent.Create(TenantId, txId, 50m);
        intent.Complete("re_001");

        var tenant = BuildTenant();
        SetupMinimalTenant(tenant, new List<Transaction> { tx });

        _disputeRepository.GetByTransactionIdAsync(Arg.Any<Guid>()).Returns((Dispute?)null);
        _refundIntentRepository.GetByTransactionIdAsync(txId).Returns(new List<RefundIntent> { intent });

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert: no mismatch (1 cent tolerance)
        var run = CaptureReconciliationRun();
        run.Issues.Should().NotContain(i => i.Type == ReconciliationIssueType.REFUND_TOTAL_MISMATCH);
    }

    [Fact]
    public async Task RunDailyReconciliation_ShouldSkipRefundCheck_WhenNoRefundIntentsExist()
    {
        // Arrange: refunded amount exists but no RefundIntents (pre-migration)
        var txId = Guid.NewGuid();
        var tx = BuildTransaction(TransactionStatus.CAPTURED, txId: txId, refundedAmount: 50m);

        var tenant = BuildTenant();
        SetupMinimalTenant(tenant, new List<Transaction> { tx });

        _disputeRepository.GetByTransactionIdAsync(Arg.Any<Guid>()).Returns((Dispute?)null);
        _refundIntentRepository.GetByTransactionIdAsync(txId).Returns(new List<RefundIntent>());

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert: no REFUND_TOTAL_MISMATCH (pre-migration skip)
        var run = CaptureReconciliationRun();
        run.Issues.Should().NotContain(i => i.Type == ReconciliationIssueType.REFUND_TOTAL_MISMATCH);
    }

    // ── LEDGER_GLOBAL_IMBALANCE Detection ──────────────────────────────

    [Fact]
    public async Task RunDailyReconciliation_ShouldDetectLedgerGlobalImbalance()
    {
        // Arrange: entry totals that don't sum to zero
        var tenant = BuildTenant();
        _tenantRepository.GetAllAsync().Returns(new List<Tenant> { tenant });
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var platformAccount = new LedgerAccountSummary(
            Guid.NewGuid(), TenantId, null, LedgerAccountType.PLATFORM_RECEIVABLE,
            CurrentBalance: 100m, SumOfEntries: 100m);
        var sellerAccount = new LedgerAccountSummary(
            Guid.NewGuid(), TenantId, SellerId, LedgerAccountType.WALLET,
            CurrentBalance: 50m, SumOfEntries: 50m);
        // Sum = 150 != 0 → global imbalance

        _ledgerRepository.GetAccountsWithEntryTotalsAsync(TenantId)
            .Returns(new List<LedgerAccountSummary> { platformAccount, sellerAccount });

        // Phases 2-5 minimal
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>()));
        _payoutRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<PayoutStatus?>())
            .Returns(new List<Payout>());
        _stripeApiClient.ListTransfersAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new StripeTransferListResponse(new List<StripeTransferItem>()));
        _stripeApiClient.GetBalanceAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new StripeBalanceResponse(
                Available: new List<StripeBalanceAmount> { new(0, "brl") },
                Pending: new List<StripeBalanceAmount> { new(0, "brl") }));

        // Return empty transactions for all queries
        _transactionRepository.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction>());

        _reconciliationRepository.GetLatestRunAsync(TenantId, "BATCH").Returns((ReconciliationRun?)null);

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert
        var run = CaptureReconciliationRun();
        run.Issues.Should().Contain(i => i.Type == ReconciliationIssueType.LEDGER_GLOBAL_IMBALANCE);
    }

    [Fact]
    public async Task RunDailyReconciliation_ShouldNotFlagGlobalImbalance_WhenBalanced()
    {
        // Arrange: entry totals sum to zero (balanced double-entry)
        var tenant = BuildTenant();
        _tenantRepository.GetAllAsync().Returns(new List<Tenant> { tenant });
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var platformCredit = new LedgerAccountSummary(
            Guid.NewGuid(), TenantId, null, LedgerAccountType.PLATFORM_RECEIVABLE,
            CurrentBalance: 0m, SumOfEntries: 0m);
        var sellerWallet = new LedgerAccountSummary(
            Guid.NewGuid(), TenantId, SellerId, LedgerAccountType.WALLET,
            CurrentBalance: 100m, SumOfEntries: 100m);
        var platformPayout = new LedgerAccountSummary(
            Guid.NewGuid(), TenantId, null, LedgerAccountType.PLATFORM_PAYOUT,
            CurrentBalance: 100m, SumOfEntries: -100m);
        // Sum = 0 + 100 + (-100) = 0 → balanced

        _ledgerRepository.GetAccountsWithEntryTotalsAsync(TenantId)
            .Returns(new List<LedgerAccountSummary> { platformCredit, sellerWallet, platformPayout });

        // Phases 2-5 minimal
        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>()));
        _payoutRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<PayoutStatus?>())
            .Returns(new List<Payout>());
        _stripeApiClient.ListTransfersAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new StripeTransferListResponse(new List<StripeTransferItem>()));
        _stripeApiClient.GetBalanceAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new StripeBalanceResponse(
                Available: new List<StripeBalanceAmount> { new(0, "brl") },
                Pending: new List<StripeBalanceAmount> { new(0, "brl") }));

        // Return empty transactions for all queries
        _transactionRepository.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction>());

        _reconciliationRepository.GetLatestRunAsync(TenantId, "BATCH").Returns((ReconciliationRun?)null);

        // Act
        await _sut.RunDailyReconciliationAsync();

        // Assert
        var run = CaptureReconciliationRun();
        run.Issues.Should().NotContain(i => i.Type == ReconciliationIssueType.LEDGER_GLOBAL_IMBALANCE);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private Transaction BuildTransaction(
        TransactionStatus status,
        Guid? paymentIntentId = null,
        Guid? txId = null,
        decimal refundedAmount = 0m)
    {
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.CREDIT_CARD, PaymentProvider.STRIPE, 1,
            feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: $"pi_{Guid.NewGuid():N}",
            sellerId: SellerId);

        var tx = result.Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(tx, txId ?? Guid.NewGuid());

        if (paymentIntentId.HasValue)
            tx.SetPaymentIntentId(paymentIntentId.Value);

        if (status == TransactionStatus.CAPTURED)
            tx.UpdateStatus(TransactionStatus.CAPTURED);
        else if (status == TransactionStatus.CHARGEBACKERROR)
        {
            tx.UpdateStatus(TransactionStatus.CAPTURED);
            typeof(Transaction).GetProperty("Status")!.SetValue(tx, TransactionStatus.CHARGEBACKERROR);
        }

        if (refundedAmount > 0)
            typeof(Transaction).GetProperty("RefundedAmount")!.SetValue(tx, refundedAmount);

        return tx;
    }

    private Tenant BuildTenant()
    {
        var tenant = Tenant.Create("TestTenant", "test", "hash", "fp_", "secret");
        typeof(Tenant).GetProperty("Id")!.SetValue(tenant, TenantId);
        var config = TenantConfig.Create(TenantId);
        typeof(Tenant).GetProperty("Config")!.SetValue(tenant, config);
        return tenant;
    }

    private void SetupMinimalTenant(Tenant tenant, List<Transaction>? phase5Transactions = null)
    {
        _tenantRepository.GetAllAsync().Returns(new List<Tenant> { tenant });
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        _ledgerRepository.GetAccountsWithEntryTotalsAsync(TenantId)
            .Returns(new List<LedgerAccountSummary>());

        _stripeApiClient.ListChargesAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeChargeListResponse(new List<StripeChargeItem>()));

        _payoutRepository.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<PayoutStatus?>())
            .Returns(new List<Payout>());
        _stripeApiClient.ListTransfersAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new StripeTransferListResponse(new List<StripeTransferItem>()));

        _stripeApiClient.GetBalanceAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new StripeBalanceResponse(
                Available: new List<StripeBalanceAmount> { new(0, "brl") },
                Pending: new List<StripeBalanceAmount> { new(0, "brl") }));

        _reconciliationRepository.GetLatestRunAsync(TenantId, "BATCH").Returns((ReconciliationRun?)null);

        // Return transactions conditionally based on provider filter
        _transactionRepository.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(callInfo =>
            {
                var provider = callInfo.ArgAt<PaymentProvider?>(4);
                return provider.HasValue ? new List<Transaction>() : (phase5Transactions ?? new List<Transaction>());
            });
    }

    /// <summary>
    /// Captures the ReconciliationRun that was passed to AddRun.
    /// Must be called AFTER the act phase.
    /// </summary>
    private ReconciliationRun CaptureReconciliationRun()
    {
        ReconciliationRun captured = null!;
        _reconciliationRepository.When(r => r.AddRun(Arg.Any<ReconciliationRun>()))
            .Do(ci => captured = ci.Arg<ReconciliationRun>());

        // Re-read: NSubstitute captures calls, we can inspect them
        var calls = _reconciliationRepository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "AddRun")
            .ToList();

        calls.Should().NotBeEmpty("Expected AddRun to have been called");
        return (ReconciliationRun)calls.Last().GetArguments()[0]!;
    }
}
