using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Subscriptions.DTOs;
using FellowCore.Application.Modules.Subscriptions.Services;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Flows;

/// <summary>
/// T18: Subscription flow tests.
/// Tests pause/resume lifecycle, dunning (missed payments), and max cycles enforcement.
/// </summary>
public class SubscriptionFlowTests
{
    // ── Subscription Service Dependencies ──────────────────────────────

    private readonly ISubscriptionRepository _subscriptionRepository = Substitute.For<ISubscriptionRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly SubscriptionService _subscriptionSut;

    // ── Billing Processor Dependencies ─────────────────────────────────

    private readonly ITransactionService _transactionService = Substitute.For<ITransactionService>();
    private readonly SubscriptionBillingProcessor _billingProcessor;

    // ── Dunning Processor Dependencies ─────────────────────────────────

    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly IPaymentProviderFactory _providerFactory = Substitute.For<IPaymentProviderFactory>();
    private readonly DunningProcessor _dunningProcessor;

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    public SubscriptionFlowTests()
    {
        _subscriptionSut = new SubscriptionService(
            _subscriptionRepository, _sellerRepository,
            Substitute.For<ILogger<SubscriptionService>>());

        _billingProcessor = new SubscriptionBillingProcessor(
            _subscriptionRepository, _sellerRepository,
            _transactionService,
            Substitute.For<ILogger<SubscriptionBillingProcessor>>());

        _dunningProcessor = new DunningProcessor(
            _transactionRepository, _providerFactory,
            Substitute.For<ILogger<DunningProcessor>>());
    }

    // ── Pause / Resume Lifecycle ───────────────────────────────────────

    [Fact]
    public async Task PauseAsync_ShouldSetStatusToPaused()
    {
        // Arrange
        var subscription = CreateActiveSubscription();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        // Act
        var result = await _subscriptionSut.PauseAsync(TenantId, subscription.Id);

        // Assert
        result.Status.Should().Be(SubscriptionStatus.PAUSED);
        subscription.Status.Should().Be(SubscriptionStatus.PAUSED);
        _subscriptionRepository.Received(1).Update(subscription);
        await _subscriptionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task PauseAsync_WhenNotActive_ShouldThrow()
    {
        // Arrange: already paused
        var subscription = CreateActiveSubscription();
        subscription.Pause();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        // Act
        var act = () => _subscriptionSut.PauseAsync(TenantId, subscription.Id);

        // Assert
        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*ativas*");
    }

    [Fact]
    public async Task ResumeAsync_ShouldSetStatusToActive()
    {
        // Arrange
        var subscription = CreateActiveSubscription();
        subscription.Pause();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        // Act
        var result = await _subscriptionSut.ResumeAsync(TenantId, subscription.Id);

        // Assert
        result.Status.Should().Be(SubscriptionStatus.ACTIVE);
        subscription.Status.Should().Be(SubscriptionStatus.ACTIVE);
        _subscriptionRepository.Received(1).Update(subscription);
    }

    [Fact]
    public async Task ResumeAsync_WhenNotPaused_ShouldThrow()
    {
        // Arrange: active subscription (not paused)
        var subscription = CreateActiveSubscription();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        // Act
        var act = () => _subscriptionSut.ResumeAsync(TenantId, subscription.Id);

        // Assert
        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*pausadas*");
    }

    [Fact]
    public async Task PauseAndResume_ShouldNotLoseCycleCount()
    {
        // Arrange
        var subscription = CreateActiveSubscription();
        subscription.AdvanceCycle(); // cycle 1
        subscription.AdvanceCycle(); // cycle 2
        var cyclesBefore = subscription.CycleCount;

        // Pause
        subscription.Pause();
        subscription.Status.Should().Be(SubscriptionStatus.PAUSED);

        // Resume
        subscription.Resume();
        subscription.Status.Should().Be(SubscriptionStatus.ACTIVE);

        // Assert: cycles preserved
        subscription.CycleCount.Should().Be(cyclesBefore);
    }

    // ── Billing Processor: Normal Billing ──────────────────────────────

    [Fact]
    public async Task BillingProcessor_ShouldCreateTransactionAndAdvanceCycle()
    {
        // Arrange: subscription due for billing
        var subscription = CreateActiveSubscription(nextBillingDate: DateTime.UtcNow.AddHours(-1));
        var seller = BuildSeller();

        _subscriptionRepository.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription> { subscription }.AsReadOnly());

        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);

        _transactionService.CreateAsync(TenantId, Arg.Any<CreateTransactionDto>())
            .Returns(new TransactionResponseDto(
                Guid.NewGuid(), TransactionStatus.PROCESSING, subscription.Amount,
                new GatewayPaymentDetails("pi_sub_001")));

        // Act
        await _billingProcessor.ProcessDueBillingAsync();

        // Assert: transaction created
        await _transactionService.Received(1).CreateAsync(TenantId, Arg.Is<CreateTransactionDto>(dto =>
            dto.Amount == subscription.Amount &&
            dto.SellerId == SellerId));

        // Assert: cycle advanced
        subscription.CycleCount.Should().Be(1);

        // Assert: next billing date moved forward
        subscription.NextBillingDate.Should().BeAfter(DateTime.UtcNow);

        // Assert: subscription updated & saved
        _subscriptionRepository.Received(1).Update(subscription);
        await _subscriptionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task BillingProcessor_ShouldSkipInactiveSeller()
    {
        // Arrange: subscription with suspended seller
        var subscription = CreateActiveSubscription(nextBillingDate: DateTime.UtcNow.AddHours(-1));
        var seller = BuildSeller();
        seller.Suspend();

        _subscriptionRepository.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription> { subscription }.AsReadOnly());

        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);

        // Act
        await _billingProcessor.ProcessDueBillingAsync();

        // Assert: no transaction created
        await _transactionService.DidNotReceive().CreateAsync(
            Arg.Any<Guid>(), Arg.Any<CreateTransactionDto>());
    }

    [Fact]
    public async Task BillingProcessor_ShouldSkipPausedSubscriptions()
    {
        // Arrange: paused subscriptions should not be returned by GetDueForBillingAsync,
        // but we test the domain's IsDueForBilling guard
        var subscription = CreateActiveSubscription(nextBillingDate: DateTime.UtcNow.AddHours(-1));
        subscription.Pause();

        // IsDueForBilling should return false
        subscription.IsDueForBilling(DateTime.UtcNow).Should().BeFalse();

        // Even if the repo returned it, billing processor won't be called since
        // the query filters by ACTIVE status
        _subscriptionRepository.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription>().AsReadOnly());

        await _billingProcessor.ProcessDueBillingAsync();

        await _transactionService.DidNotReceive().CreateAsync(
            Arg.Any<Guid>(), Arg.Any<CreateTransactionDto>());
    }

    // ── Max Cycles Enforcement ─────────────────────────────────────────

    [Fact]
    public void AdvanceCycle_WhenMaxCyclesReached_ShouldExpireSubscription()
    {
        // Arrange: subscription with maxCycles = 3
        var subscription = CreateActiveSubscription(maxCycles: 3);

        // Act: advance 3 cycles
        subscription.AdvanceCycle(); // 1
        subscription.AdvanceCycle(); // 2
        subscription.AdvanceCycle(); // 3 → should expire

        // Assert
        subscription.CycleCount.Should().Be(3);
        subscription.Status.Should().Be(SubscriptionStatus.EXPIRED);
        subscription.EndDate.Should().NotBeNull();
    }

    [Fact]
    public void AdvanceCycle_BeforeMaxCycles_ShouldRemainActive()
    {
        var subscription = CreateActiveSubscription(maxCycles: 5);

        subscription.AdvanceCycle(); // 1
        subscription.AdvanceCycle(); // 2

        subscription.CycleCount.Should().Be(2);
        subscription.Status.Should().Be(SubscriptionStatus.ACTIVE);
        subscription.EndDate.Should().BeNull();
    }

    [Fact]
    public void AdvanceCycle_WithNoMaxCycles_ShouldNeverExpire()
    {
        var subscription = CreateActiveSubscription(maxCycles: null);

        for (int i = 0; i < 100; i++)
            subscription.AdvanceCycle();

        subscription.CycleCount.Should().Be(100);
        subscription.Status.Should().Be(SubscriptionStatus.ACTIVE);
    }

    [Fact]
    public async Task BillingProcessor_ShouldExpireAfterFinalCycle()
    {
        // Arrange: subscription at cycle 2 of maxCycles 3
        var subscription = CreateActiveSubscription(
            nextBillingDate: DateTime.UtcNow.AddHours(-1), maxCycles: 3);
        subscription.AdvanceCycle(); // 1
        subscription.AdvanceCycle(); // 2

        var seller = BuildSeller();

        _subscriptionRepository.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription> { subscription }.AsReadOnly());

        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);

        _transactionService.CreateAsync(TenantId, Arg.Any<CreateTransactionDto>())
            .Returns(new TransactionResponseDto(
                Guid.NewGuid(), TransactionStatus.PROCESSING, subscription.Amount,
                new GatewayPaymentDetails("pi_sub_final")));

        // Act
        await _billingProcessor.ProcessDueBillingAsync();

        // Assert: cycle 3 → EXPIRED
        subscription.CycleCount.Should().Be(3);
        subscription.Status.Should().Be(SubscriptionStatus.EXPIRED);
    }

    // ── Dunning (Missed Payments) ──────────────────────────────────────

    [Fact]
    public void Transaction_DeclinedOnFirstAttempt_ShouldScheduleDunning()
    {
        // Create a transaction and transition to DECLINED
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE, 1,
            feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_dunning");

        var tx = result.Value;
        tx.UpdateStatus(TransactionStatus.DECLINED);

        // Assert: dunning scheduled
        tx.NextDunningAt.Should().NotBeNull();
        tx.DunningAttempts.Should().Be(0);
    }

    [Fact]
    public void RecordDunningAttempt_FailedAndBelowMax_ShouldReschedule()
    {
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE, 1,
            feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_dunning_2");

        var tx = result.Value;
        tx.UpdateStatus(TransactionStatus.DECLINED);

        // First dunning attempt fails
        tx.RecordDunningAttempt(false);

        tx.DunningAttempts.Should().Be(1);
        tx.NextDunningAt.Should().NotBeNull("should be rescheduled for retry");
    }

    [Fact]
    public void RecordDunningAttempt_Succeeded_ShouldClearNextDunning()
    {
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE, 1,
            feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_dunning_3");

        var tx = result.Value;
        tx.UpdateStatus(TransactionStatus.DECLINED);

        tx.RecordDunningAttempt(true);

        tx.DunningAttempts.Should().Be(1);
        tx.NextDunningAt.Should().BeNull("successful dunning clears next attempt");
    }

    [Fact]
    public void RecordDunningAttempt_ExhaustsMaxAttempts_ShouldClearNextDunning()
    {
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE, 1,
            feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_dunning_4");

        var tx = result.Value;
        tx.UpdateStatus(TransactionStatus.DECLINED);

        // Exhaust all 4 attempts
        for (int i = 0; i < Transaction.MaxDunningAttempts; i++)
            tx.RecordDunningAttempt(false);

        tx.DunningAttempts.Should().Be(Transaction.MaxDunningAttempts);
        tx.NextDunningAt.Should().BeNull("max attempts exhausted");
    }

    [Fact]
    public async Task DunningProcessor_ShouldRetryPaymentForEligibleTransactions()
    {
        // Arrange
        var txResult = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE, 1,
            feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_dunning_proc");
        var tx = txResult.Value;
        tx.UpdateStatus(TransactionStatus.DECLINED);

        // Set NextDunningAt to the past so it's eligible
        typeof(Transaction).GetProperty("NextDunningAt")!.SetValue(tx, DateTime.UtcNow.AddMinutes(-1));

        _transactionRepository.GetDunningEligibleAsync(Arg.Any<DateTime>())
            .Returns(new List<Transaction> { tx });

        var provider = Substitute.For<IPaymentProvider>();
        provider.ProcessPaymentAsync(
            Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<CreateTransactionDto>(),
            Arg.Any<decimal>(), Arg.Any<string?>())
            .Returns(new GatewayPaymentDetails("pi_retry_001"));

        _providerFactory.GetProvider(PaymentProvider.STRIPE).Returns(provider);

        // Act
        await _dunningProcessor.ProcessDunningAsync();

        // Assert: provider called for retry
        await provider.Received(1).ProcessPaymentAsync(
            Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<CreateTransactionDto>(),
            Arg.Any<decimal>(), Arg.Any<string?>());

        // Assert: dunning attempt recorded and status updated
        tx.DunningAttempts.Should().Be(1);

        // Assert: transaction updated
        _transactionRepository.Received(1).Update(tx);
        await _transactionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task DunningProcessor_WhenProviderFails_ShouldRecordFailedAttempt()
    {
        // Arrange
        var txResult = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE, 1,
            feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_dunning_fail");
        var tx = txResult.Value;
        tx.UpdateStatus(TransactionStatus.DECLINED);
        typeof(Transaction).GetProperty("NextDunningAt")!.SetValue(tx, DateTime.UtcNow.AddMinutes(-1));

        _transactionRepository.GetDunningEligibleAsync(Arg.Any<DateTime>())
            .Returns(new List<Transaction> { tx });

        var provider = Substitute.For<IPaymentProvider>();
        provider.ProcessPaymentAsync(
            Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<CreateTransactionDto>(),
            Arg.Any<decimal>(), Arg.Any<string?>())
            .Throws(new Exception("Payment failed"));

        _providerFactory.GetProvider(PaymentProvider.STRIPE).Returns(provider);

        // Act
        await _dunningProcessor.ProcessDunningAsync();

        // Assert: failed attempt recorded
        tx.DunningAttempts.Should().Be(1);

        // Assert: transaction saved (even on failure)
        _transactionRepository.Received(1).Update(tx);
        await _transactionRepository.Received(1).SaveChangesAsync();
    }

    // ── Subscription Cancel ────────────────────────────────────────────

    [Fact]
    public async Task CancelAsync_ShouldSetStatusToCanceled()
    {
        var subscription = CreateActiveSubscription();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        var result = await _subscriptionSut.CancelAsync(TenantId, subscription.Id);

        result.Status.Should().Be(SubscriptionStatus.CANCELED);
        subscription.EndDate.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelAsync_AlreadyCanceled_ShouldRemainCanceled()
    {
        var subscription = CreateActiveSubscription();
        subscription.Cancel();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        // Calling cancel on already-canceled should still return CANCELED (idempotent)
        var result = await _subscriptionSut.CancelAsync(TenantId, subscription.Id);
        result.Status.Should().Be(SubscriptionStatus.CANCELED);
    }

    // ── L13: Subscription Billing Ledger Path ──────────────────────────

    [Fact]
    public async Task BillingProcessor_LedgerEntriesViaTransaction_NotIntermediate()
    {
        // L13: Subscription billing creates a standard transaction via TransactionService.
        // Ledger entries are recorded on CAPTURE (via webhook), not at billing time.
        // This test confirms the billing processor ONLY calls TransactionService.CreateAsync —
        // no direct ledger calls — validating the architectural decision.

        var subscription = CreateActiveSubscription(nextBillingDate: DateTime.UtcNow.AddHours(-1));
        var seller = BuildSeller();

        _subscriptionRepository.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription> { subscription }.AsReadOnly());
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _transactionService.CreateAsync(TenantId, Arg.Any<CreateTransactionDto>())
            .Returns(new TransactionResponseDto(
                Guid.NewGuid(), TransactionStatus.PROCESSING, subscription.Amount,
                new GatewayPaymentDetails("pi_sub_ledger")));

        await _billingProcessor.ProcessDueBillingAsync();

        // Assert: TransactionService called (standard flow handles ledger on capture)
        await _transactionService.Received(1).CreateAsync(TenantId, Arg.Any<CreateTransactionDto>());

        // Assert: idempotency key includes subscription ID and cycle
        await _transactionService.Received(1).CreateAsync(TenantId, Arg.Is<CreateTransactionDto>(dto =>
            dto.IdempotencyKey != null && dto.IdempotencyKey.StartsWith($"sub-{subscription.Id:N}-cycle-")));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private Subscription CreateActiveSubscription(
        DateTime? nextBillingDate = null, int? maxCycles = null)
    {
        var result = Subscription.Create(
            TenantId, SellerId, 99.90m,
            "Monthly Plan",
            BillingInterval.MONTHLY,
            startDate: DateTime.UtcNow.AddMonths(-1),
            maxCycles: maxCycles);

        var subscription = result.Value;

        if (nextBillingDate.HasValue)
            typeof(Subscription).GetProperty("NextBillingDate")!.SetValue(subscription, nextBillingDate.Value);

        return subscription;
    }

    private static Seller BuildSeller()
    {
        return Seller.Create(
            TenantId, "Seller Ltda", "12345678000100", "seller@test.com",
            "secret-123", PaymentProvider.STRIPE, "acct_123");
    }
}
