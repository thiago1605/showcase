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
/// Tests for Reconciliation Phase 6 — Split integrity checks.
/// Covers SPLIT_TOTAL_MISMATCH, SPLIT_DUPLICATE_CREDIT, SPLIT_REFUND_NOT_REVERSED.
/// </summary>
public class SplitReconciliationTests
{
    private readonly IReconciliationRepository _reconciliationRepository = Substitute.For<IReconciliationRepository>();
    private readonly ILedgerRepository _ledgerRepository = Substitute.For<ILedgerRepository>();
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly IPayoutRepository _payoutRepository = Substitute.For<IPayoutRepository>();
    private readonly IDisputeRepository _disputeRepository = Substitute.For<IDisputeRepository>();
    private readonly IRefundIntentRepository _refundIntentRepository = Substitute.For<IRefundIntentRepository>();
    private readonly ISplitTransferRepository _splitTransferRepository = Substitute.For<ISplitTransferRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly IStripeApiClient _stripeApiClient = Substitute.For<IStripeApiClient>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IAlertService _alertService = Substitute.For<IAlertService>();
    private readonly IConfiguration _configuration;
    private readonly ReconciliationService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();
    private static readonly Guid Recipient1 = Guid.NewGuid();
    private static readonly Guid Recipient2 = Guid.NewGuid();

    public SplitReconciliationTests()
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
            _splitTransferRepository, _sellerRepository, _stripeApiClient,
            _tenantRepository, Substitute.For<ILedgerService>(),
            _alertService, _configuration,
            Substitute.For<ILogger<ReconciliationService>>());

        SetupDefaultMocks();
    }

    // ── SPLIT_TOTAL_MISMATCH: transfers exceed net ────────────────────────

    [Fact]
    public async Task ShouldDetect_SplitTotalMismatch_WhenTransfersExceedNet()
    {
        var tx = BuildCapturedTransaction(netAmount: 100m);

        _transactionRepository.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(callInfo =>
            {
                var provider = callInfo.ArgAt<PaymentProvider?>(4);
                return provider.HasValue ? new List<Transaction>() : new List<Transaction> { tx };
            });

        // SplitTransfers total = 120 > net 100 → CRITICAL
        var transfer1 = BuildSplitTransfer(tx.Id, Recipient1, 70m, SplitTransferStatus.RESERVED);
        var transfer2 = BuildSplitTransfer(tx.Id, Recipient2, 50m, SplitTransferStatus.RESERVED);
        _splitTransferRepository.GetByTransactionIdAsync(TenantId, tx.Id)
            .Returns(new List<SplitTransfer> { transfer1, transfer2 });

        ReconciliationRun? savedRun = null;
        _reconciliationRepository.When(r => r.AddRun(Arg.Any<ReconciliationRun>()))
            .Do(ci => savedRun = ci.ArgAt<ReconciliationRun>(0));

        await _sut.RunDailyReconciliationAsync();

        savedRun.Should().NotBeNull();
        savedRun!.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.SPLIT_TOTAL_MISMATCH &&
            i.Severity == "CRITICAL");
    }

    // ── SPLIT_TOTAL_MISMATCH: transfers within net → no issue ─────────────

    [Fact]
    public async Task ShouldNotFlag_WhenTransfersWithinNet()
    {
        var tx = BuildCapturedTransaction(netAmount: 100m);

        _transactionRepository.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(callInfo =>
            {
                var provider = callInfo.ArgAt<PaymentProvider?>(4);
                return provider.HasValue ? new List<Transaction>() : new List<Transaction> { tx };
            });

        // Total = 95 <= net 100 → OK
        var transfer1 = BuildSplitTransfer(tx.Id, Recipient1, 60m, SplitTransferStatus.RESERVED);
        var transfer2 = BuildSplitTransfer(tx.Id, Recipient2, 35m, SplitTransferStatus.PAID);
        _splitTransferRepository.GetByTransactionIdAsync(TenantId, tx.Id)
            .Returns(new List<SplitTransfer> { transfer1, transfer2 });

        ReconciliationRun? savedRun = null;
        _reconciliationRepository.When(r => r.AddRun(Arg.Any<ReconciliationRun>()))
            .Do(ci => savedRun = ci.ArgAt<ReconciliationRun>(0));

        await _sut.RunDailyReconciliationAsync();

        savedRun.Should().NotBeNull();
        savedRun!.Issues.Should().NotContain(i => i.Type == ReconciliationIssueType.SPLIT_TOTAL_MISMATCH);
    }

    // ── SPLIT_DUPLICATE_CREDIT: same recipient twice ──────────────────────

    [Fact]
    public async Task ShouldDetect_SplitDuplicateCredit_WhenRecipientHasMultipleTransfers()
    {
        var tx = BuildCapturedTransaction(netAmount: 100m);

        _transactionRepository.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(callInfo =>
            {
                var provider = callInfo.ArgAt<PaymentProvider?>(4);
                return provider.HasValue ? new List<Transaction>() : new List<Transaction> { tx };
            });

        // Same recipient with 2 non-failed transfers → DUPLICATE
        var transfer1 = BuildSplitTransfer(tx.Id, Recipient1, 50m, SplitTransferStatus.RESERVED);
        var transfer2 = BuildSplitTransfer(tx.Id, Recipient1, 30m, SplitTransferStatus.PAID);
        _splitTransferRepository.GetByTransactionIdAsync(TenantId, tx.Id)
            .Returns(new List<SplitTransfer> { transfer1, transfer2 });

        ReconciliationRun? savedRun = null;
        _reconciliationRepository.When(r => r.AddRun(Arg.Any<ReconciliationRun>()))
            .Do(ci => savedRun = ci.ArgAt<ReconciliationRun>(0));

        await _sut.RunDailyReconciliationAsync();

        savedRun.Should().NotBeNull();
        savedRun!.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.SPLIT_DUPLICATE_CREDIT &&
            i.Severity == "CRITICAL");
    }

    // ── SPLIT_DUPLICATE_CREDIT: failed duplicate is OK ────────────────────

    [Fact]
    public async Task ShouldNotFlag_WhenDuplicateTransferIsFailed()
    {
        var tx = BuildCapturedTransaction(netAmount: 100m);

        _transactionRepository.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(callInfo =>
            {
                var provider = callInfo.ArgAt<PaymentProvider?>(4);
                return provider.HasValue ? new List<Transaction>() : new List<Transaction> { tx };
            });

        // Same recipient: 1 RESERVED + 1 FAILED → only 1 non-failed → OK
        var transfer1 = BuildSplitTransfer(tx.Id, Recipient1, 50m, SplitTransferStatus.RESERVED);
        var failedTransfer = BuildSplitTransfer(tx.Id, Recipient1, 50m, SplitTransferStatus.FAILED);
        _splitTransferRepository.GetByTransactionIdAsync(TenantId, tx.Id)
            .Returns(new List<SplitTransfer> { transfer1, failedTransfer });

        ReconciliationRun? savedRun = null;
        _reconciliationRepository.When(r => r.AddRun(Arg.Any<ReconciliationRun>()))
            .Do(ci => savedRun = ci.ArgAt<ReconciliationRun>(0));

        await _sut.RunDailyReconciliationAsync();

        savedRun.Should().NotBeNull();
        savedRun!.Issues.Should().NotContain(i => i.Type == ReconciliationIssueType.SPLIT_DUPLICATE_CREDIT);
    }

    // ── SPLIT_REFUND_NOT_REVERSED: refunded TX with active splits ─────────

    [Fact]
    public async Task ShouldDetect_SplitRefundNotReversed_WhenRefundedTxHasActiveTransfers()
    {
        var tx = BuildRefundedTransaction();

        _transactionRepository.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(callInfo =>
            {
                var provider = callInfo.ArgAt<PaymentProvider?>(4);
                return provider.HasValue ? new List<Transaction>() : new List<Transaction> { tx };
            });

        // REFUNDED transaction with non-reversed splits → CRITICAL
        var transfer = BuildSplitTransfer(tx.Id, Recipient1, 50m, SplitTransferStatus.PAID);
        _splitTransferRepository.GetByTransactionIdAsync(TenantId, tx.Id)
            .Returns(new List<SplitTransfer> { transfer });

        ReconciliationRun? savedRun = null;
        _reconciliationRepository.When(r => r.AddRun(Arg.Any<ReconciliationRun>()))
            .Do(ci => savedRun = ci.ArgAt<ReconciliationRun>(0));

        await _sut.RunDailyReconciliationAsync();

        savedRun.Should().NotBeNull();
        savedRun!.Issues.Should().Contain(i =>
            i.Type == ReconciliationIssueType.SPLIT_REFUND_NOT_REVERSED &&
            i.Severity == "CRITICAL");
    }

    // ── SPLIT_REFUND_NOT_REVERSED: all reversed → no issue ───────────────

    [Fact]
    public async Task ShouldNotFlag_WhenRefundedTxHasAllTransfersReversed()
    {
        var tx = BuildRefundedTransaction();

        _transactionRepository.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(callInfo =>
            {
                var provider = callInfo.ArgAt<PaymentProvider?>(4);
                return provider.HasValue ? new List<Transaction>() : new List<Transaction> { tx };
            });

        // All transfers REVERSED → OK
        var transfer = BuildSplitTransfer(tx.Id, Recipient1, 50m, SplitTransferStatus.REVERSED);
        _splitTransferRepository.GetByTransactionIdAsync(TenantId, tx.Id)
            .Returns(new List<SplitTransfer> { transfer });

        ReconciliationRun? savedRun = null;
        _reconciliationRepository.When(r => r.AddRun(Arg.Any<ReconciliationRun>()))
            .Do(ci => savedRun = ci.ArgAt<ReconciliationRun>(0));

        await _sut.RunDailyReconciliationAsync();

        savedRun.Should().NotBeNull();
        savedRun!.Issues.Should().NotContain(i => i.Type == ReconciliationIssueType.SPLIT_REFUND_NOT_REVERSED);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void SetupDefaultMocks()
    {
        var tenant = BuildTenant();
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

        _refundIntentRepository.GetByTransactionIdAsync(Arg.Any<Guid>())
            .Returns(new List<RefundIntent>());

        _reconciliationRepository.GetLatestRunAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns((ReconciliationRun?)null);
    }

    private static Tenant BuildTenant()
    {
        var tenant = Tenant.Create("TestTenant", "test", "hash", "fp_", "secret");
        typeof(Tenant).GetProperty("Id")!.SetValue(tenant, TenantId);
        var config = TenantConfig.Create(TenantId);
        typeof(Tenant).GetProperty("Config")!.SetValue(tenant, config);
        return tenant;
    }

    private static Transaction BuildCapturedTransaction(decimal netAmount = 100m)
    {
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.CREDIT_CARD, PaymentProvider.STRIPE,
            1, 100m - netAmount, netAmount, DateTime.UtcNow.AddDays(30),
            $"pi_{Guid.NewGuid():N}", SellerId);
        var tx = result.Value;
        tx.UpdateStatus(TransactionStatus.CAPTURED);
        return tx;
    }

    private static Transaction BuildRefundedTransaction()
    {
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.CREDIT_CARD, PaymentProvider.STRIPE,
            1, 2m, 98m, DateTime.UtcNow.AddDays(30),
            $"pi_{Guid.NewGuid():N}", SellerId);
        var tx = result.Value;
        tx.UpdateStatus(TransactionStatus.CAPTURED);
        tx.Refund(100m); // Full refund → REFUNDED status
        return tx;
    }

    private static SplitTransfer BuildSplitTransfer(Guid txId, Guid recipientId, decimal amount, SplitTransferStatus targetStatus)
    {
        var createResult = SplitTransfer.Create(txId, TenantId, recipientId, amount);
        var transfer = createResult.Value;
        if (targetStatus >= SplitTransferStatus.RESERVED) transfer.Reserve();
        if (targetStatus >= SplitTransferStatus.PAID) transfer.MarkPaid();
        if (targetStatus == SplitTransferStatus.REVERSED) transfer.Reverse();
        if (targetStatus == SplitTransferStatus.FAILED) transfer.Fail("test failure");
        return transfer;
    }
}
