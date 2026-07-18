using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Payouts.DTOs;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Application.Modules.Payouts.Services;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Application.Modules.Splits.Services;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Flows;

/// <summary>
/// T14: Payout failure/reversal and multi-seller split flow tests.
/// Validates payout failure -> full ledger reversal (net + fee),
/// and multi-seller split payout scenarios via SplitProcessor.
/// </summary>
public class PayoutFlowTests
{
    // ── Payout Service Dependencies ────────────────────────────────────

    private readonly IPayoutRepository _payoutRepository = Substitute.For<IPayoutRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly IPayoutProcessor _payoutProcessor = Substitute.For<IPayoutProcessor>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IRealtimeNotifier _realtimeNotifier = Substitute.For<IRealtimeNotifier>();
    private readonly IBackgroundJobs _backgroundJobs = Substitute.For<IBackgroundJobs>();
    private readonly PayoutService _payoutSut;

    // ── Split Processor Dependencies ───────────────────────────────────

    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly SplitProcessor _splitSut;

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    public PayoutFlowTests()
    {
        _payoutSut = new PayoutService(
            _payoutRepository, _sellerRepository, _tenantRepository,
            _ledgerService, _payoutProcessor, _emailService,
            _realtimeNotifier, _backgroundJobs,
            Substitute.For<IAppMetrics>(),
            Substitute.For<ILogger<PayoutService>>());

        // SplitProcessor now uses ISplitTransferRepository, ILedgerService, and
        // ISplitCalculationService instead of IPayoutService. Payout flows are tested
        // directly via _payoutSut above; here we validate split orchestration.
        var itemResolver = Substitute.For<IItemSplitResolver>();
        itemResolver.ResolveFromItemsAsync(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(new ItemSplitResolution([], HasItemSplits: false));
        _splitSut = new SplitProcessor(
            _transactionRepository,
            Substitute.For<ISplitTransferRepository>(),
            Substitute.For<ISplitAllocationRepository>(),
            _ledgerService,
            new SplitCalculationService(),
            itemResolver,
            Substitute.For<ILogger<SplitProcessor>>());
    }

    // ── Payout Failure → Full Ledger Reversal (Net + Fee) ──────────────

    [Fact]
    public async Task PayoutFailure_ShouldScheduleRetry_WhenProviderFails()
    {
        // Arrange
        var seller = BuildSeller(payoutFixedFee: 2m, payoutPercentFee: 0m);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(BuildTenant());

        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Returns(new PayoutResult(false, FailureReason: "Provider offline"));

        // Act
        var result = await _payoutSut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        // Assert: first failure schedules retry — stays PROCESSING
        result.Status.Should().Be(PayoutStatus.PROCESSING);

        // Assert: ledger debit was called (stays in place for retry)
        await _ledgerService.Received(1).DebitSellerAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());

        // Assert: NO reversal yet — retry pending
        await _ledgerService.DidNotReceive().ReversalCreditAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PayoutFailure_Exception_ShouldReverseNetAndFee()
    {
        // Arrange
        var seller = BuildSeller(payoutFixedFee: 2m, payoutPercentFee: 0m);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(BuildTenant());

        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Throws(new Exception("Network error"));

        // Act
        var result = await _payoutSut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        // Assert: payout failed
        result.Status.Should().Be(PayoutStatus.FAILED);

        // Assert: reversal for net
        await _ledgerService.Received(1).ReversalCreditAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());

        // Assert: reversal for fee
        await _ledgerService.Received(1).ReversePayoutFeeAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PayoutFailure_WhenReversalFails_ShouldEnqueueBackgroundRetry()
    {
        // Arrange
        var seller = BuildSeller(payoutFixedFee: 2m, payoutPercentFee: 0m);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(BuildTenant());

        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Throws(new Exception("Network error"));

        _ledgerService.ReversalCreditAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>())
            .Throws(new Exception("Database error"));

        // Act
        var result = await _payoutSut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        // Assert: background retry enqueued for compensation
        _backgroundJobs.Received(1).Enqueue<ILedgerService>(Arg.Any<System.Linq.Expressions.Expression<Func<ILedgerService, Task>>>());
    }

    [Fact]
    public async Task PayoutSuccess_ShouldDebitLedgerAndCompleteWithTransactionId()
    {
        // Arrange
        var seller = BuildSeller(payoutFixedFee: 2m, payoutPercentFee: 0m);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(BuildTenant());

        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Returns(new PayoutResult(true, TransactionId: "bank-tx-001"));

        // Act
        var result = await _payoutSut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        // Assert: success
        result.Status.Should().Be(PayoutStatus.PAID);

        // Assert: ledger debited for net
        await _ledgerService.Received(1).DebitSellerAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());

        // Assert: fee debited separately
        await _ledgerService.Received(1).DebitPayoutFeeAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());

        // Assert: NO reversal on success
        await _ledgerService.DidNotReceive().ReversalCreditAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PayoutSuccess_ShouldEnqueueReconciliation()
    {
        // Arrange
        var seller = BuildSeller(payoutFixedFee: 2m, payoutPercentFee: 0m);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(BuildTenant());

        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Returns(new PayoutResult(true, TransactionId: "bank-tx-001"));

        // Act
        await _payoutSut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        // Assert: reconciliation enqueued
        _backgroundJobs.Received(1).Enqueue<IReconciliationService>(
            Arg.Any<System.Linq.Expressions.Expression<Func<IReconciliationService, Task>>>());
    }

    // ── Multi-Seller Split Payout ──────────────────────────────────────

    [Fact]
    public async Task SplitProcessor_ShouldCreatePayoutsForAllPendingSplits()
    {
        // Arrange: transaction with 2 splits
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();
        var txId = Guid.NewGuid();

        var transaction = BuildTransactionWithSplits(txId, sellerA, sellerB);
        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(transaction);

        var splitTransferRepo = Substitute.For<ISplitTransferRepository>();
        splitTransferRepo
            .GetByTransactionAndRecipientAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns((SplitTransfer?)null);

        var resolver = Substitute.For<IItemSplitResolver>();
        resolver.ResolveFromItemsAsync(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(new ItemSplitResolution([], HasItemSplits: false));
        var splitProcessor = new SplitProcessor(
            _transactionRepository,
            splitTransferRepo,
            Substitute.For<ISplitAllocationRepository>(),
            _ledgerService,
            new SplitCalculationService(),
            resolver,
            Substitute.For<ILogger<SplitProcessor>>());

        // Act
        await splitProcessor.ProcessSplitsForTransactionAsync(txId);

        // Assert: distribute from clearing to both recipients (primary share is 0 since 80+110=190=netAmount)
        await _ledgerService.Received(2).DistributeFromClearingAsync(
            TenantId, Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());

        // Assert: splits marked as PAID
        transaction.Splits.Should().AllSatisfy(s =>
            s.Status.Should().Be(SplitStatus.PAID));

        // Assert: transaction updated & saved
        _transactionRepository.Received(1).Update(transaction);
        await _transactionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task SplitProcessor_WhenOnePayoutFails_ShouldMarkSplitAsFailed_OthersSucceed()
    {
        // Arrange
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();
        var txId = Guid.NewGuid();

        var transaction = BuildTransactionWithSplits(txId, sellerA, sellerB);
        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(transaction);

        var splitTransferRepo = Substitute.For<ISplitTransferRepository>();
        splitTransferRepo
            .GetByTransactionAndRecipientAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns((SplitTransfer?)null);

        var ledgerService = Substitute.For<ILedgerService>();
        var callCount = 0;
        ledgerService
            .DistributeFromClearingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                    throw new Exception("Ledger error for seller A");
                return Task.CompletedTask;
            });

        var resolver2 = Substitute.For<IItemSplitResolver>();
        resolver2.ResolveFromItemsAsync(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(new ItemSplitResolution([], HasItemSplits: false));
        var splitProcessor = new SplitProcessor(
            _transactionRepository,
            splitTransferRepo,
            Substitute.For<ISplitAllocationRepository>(),
            ledgerService,
            new SplitCalculationService(),
            resolver2,
            Substitute.For<ILogger<SplitProcessor>>());

        // Act
        await splitProcessor.ProcessSplitsForTransactionAsync(txId);

        // Assert: one PENDING (kept for retry), one PAID
        var splits = transaction.Splits.ToList();
        splits.Should().ContainSingle(s => s.Status == SplitStatus.PENDING);
        splits.Should().ContainSingle(s => s.Status == SplitStatus.PAID);
    }

    [Fact]
    public async Task SplitProcessor_ShouldIgnoreNonCapturedTransaction()
    {
        // Arrange: transaction in PROCESSING status
        var txId = Guid.NewGuid();
        var transaction = BuildTransactionWithSplits(txId, Guid.NewGuid(), Guid.NewGuid());
        typeof(Transaction).GetProperty("Status")!.SetValue(transaction, TransactionStatus.PROCESSING);
        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(transaction);

        var ledgerService = Substitute.For<ILedgerService>();
        var resolver3 = Substitute.For<IItemSplitResolver>();
        resolver3.ResolveFromItemsAsync(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(new ItemSplitResolution([], HasItemSplits: false));

        var splitProcessor = new SplitProcessor(
            _transactionRepository,
            Substitute.For<ISplitTransferRepository>(),
            Substitute.For<ISplitAllocationRepository>(),
            ledgerService,
            new SplitCalculationService(),
            resolver3,
            Substitute.For<ILogger<SplitProcessor>>());

        // Act
        await splitProcessor.ProcessSplitsForTransactionAsync(txId);

        // Assert: no ledger entries
        await ledgerService.DidNotReceive().DistributeFromClearingAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SplitProcessor_ShouldMarkInvalidRecipientAsFailed()
    {
        // Arrange: split with non-GUID recipientId
        var txId = Guid.NewGuid();
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE, 1,
            feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_test",
            sellerId: SellerId);
        var tx = result.Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(tx, txId);
        tx.UpdateStatus(TransactionStatus.CAPTURED);
        tx.AddSplit("not-a-guid", "SELLER", 50m);

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        var ledgerService = Substitute.For<ILedgerService>();
        var resolver4 = Substitute.For<IItemSplitResolver>();
        resolver4.ResolveFromItemsAsync(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(new ItemSplitResolution([], HasItemSplits: false));

        var splitProcessor = new SplitProcessor(
            _transactionRepository,
            Substitute.For<ISplitTransferRepository>(),
            Substitute.For<ISplitAllocationRepository>(),
            ledgerService,
            new SplitCalculationService(),
            resolver4,
            Substitute.For<ILogger<SplitProcessor>>());

        // Act
        await splitProcessor.ProcessSplitsForTransactionAsync(txId);

        // Assert: split marked as failed
        tx.Splits.First().Status.Should().Be(SplitStatus.FAILED);
        // Primary seller gets full netAmount from SPLIT_CLEARING since no valid splits succeeded
        await ledgerService.Received(1).DistributeFromClearingAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Seller BuildSeller(decimal payoutFixedFee = 1m, decimal payoutPercentFee = 1.5m)
    {
        return Seller.Create(
            TenantId, "Seller Ltda", "12345678000100", "seller@test.com",
            "secret-123", PaymentProvider.STRIPE, "acct_123", null,
            "Seller", "1199999", "pix@seller.com");
    }

    private static Tenant BuildTenant()
    {
        var tenant = Tenant.Create("TestTenant", "test", "hash", "fp_", "secret");
        typeof(Tenant).GetProperty("Id")!.SetValue(tenant, TenantId);
        var config = TenantConfig.Create(TenantId);
        typeof(Tenant).GetProperty("Config")!.SetValue(tenant, config);
        return tenant;
    }

    private static Transaction BuildTransactionWithSplits(Guid txId, Guid sellerA, Guid sellerB)
    {
        var result = Transaction.Create(
            TenantId, 200m, PaymentType.CREDIT_CARD, PaymentProvider.STRIPE, 1,
            feeAmount: 10m, netAmount: 190m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_split_test",
            sellerId: SellerId);

        var tx = result.Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(tx, txId);
        tx.UpdateStatus(TransactionStatus.CAPTURED);

        tx.AddSplit(sellerA.ToString(), "SELLER", 80m);
        tx.AddSplit(sellerB.ToString(), "SELLER", 110m);

        return tx;
    }
}
