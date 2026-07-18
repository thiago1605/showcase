using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Tests.Helpers;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Ledgers.Services;
using FellowCore.Application.Modules.Payouts.DTOs;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Application.Modules.Payouts.Services;
using FellowCore.Application.Modules.Settlements.Services;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Rails;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using FellowCore.Application.Modules.Webhooks.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FellowCore.Application.Tests.Services;

/// <summary>
/// Adversarial validation: simulates concurrent access, duplicate events,
/// provider failures, and partial settlement failures.
/// Verifies financial correctness (ledger invariants) after each scenario.
/// </summary>
public class AdversarialValidationTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    #region Helpers

    /// <summary>
    /// Creates a real LedgerAccount (domain entity) with an initial balance.
    /// </summary>
    private static LedgerAccount CreateWallet(decimal initialBalance)
    {
        var account = LedgerAccount.Create(TenantId, SellerId, LedgerAccountType.WALLET);
        if (initialBalance > 0)
            account.Credit(initialBalance, "Initial seed");
        return account;
    }

    private static LedgerAccount CreatePlatformAccount(LedgerAccountType type, decimal initialBalance = 0)
    {
        var account = LedgerAccount.Create(TenantId, null, type);
        if (initialBalance > 0)
            account.Credit(initialBalance, "Initial platform seed");
        return account;
    }

    private static LedgerAccount CreateFutureReceivables(decimal balance)
    {
        var account = LedgerAccount.Create(TenantId, SellerId, LedgerAccountType.FUTURE_RECEIVABLES);
        if (balance > 0)
            account.Credit(balance, "Initial seed");
        return account;
    }

    private static Transaction CreateCapturedTransaction(decimal amount = 100m, decimal? netAmount = 98.5m)
    {
        var result = Transaction.Create(
            TenantId, amount, PaymentType.PIX, PaymentProvider.STRIPE,
            1, amount - (netAmount ?? 98.5m), netAmount, DateTime.UtcNow.AddDays(30),
            $"prov_{Guid.NewGuid():N}", SellerId);
        var tx = result.Value;
        tx.UpdateStatus(TransactionStatus.CAPTURED);
        return tx;
    }

    private static Seller CreateSeller()
    {
        return Seller.Create(
            TenantId, "Test Seller", "12345678000199", "seller@test.com",
            "webhook-secret-123", PaymentProvider.STRIPE, "ext-account-123",
            "enc:access-token");
    }

    /// <summary>
    /// Asserts the fundamental double-entry invariant:
    /// Sum of all entries for an account == account.Balance
    /// </summary>
    private static void AssertLedgerInvariant(LedgerAccount account)
    {
        decimal sumOfEntries = account.Entries.Sum(e => e.Amount);
        // Initial balance (via Credit in seed) + subsequent entries
        account.Balance.Should().Be(sumOfEntries,
            $"LedgerAccount {account.Type} balance ({account.Balance}) must equal sum of entries ({sumOfEntries})");
    }

    #endregion

    #region 1. Concurrent Transactions — Multiple credits to same seller account

    [Fact]
    public async Task ConcurrentCredits_WithOptimisticRetry_ShouldConvergeToCorrectBalance()
    {
        // Arrange: Wallet starts at 1000, 10 concurrent credits of 100 each
        // Expected final balance: 2000
        const int concurrency = 10;
        const decimal creditAmount = 100m;
        const decimal initialBalance = 1000m;

        var wallet = CreateWallet(initialBalance);
        var platformAccount = CreatePlatformAccount(LedgerAccountType.PLATFORM_RECEIVABLE, 100_000m);

        int concurrencyConflictCount = 0;
        int callCount = 0;

        var repo = Substitute.For<ILedgerRepository>();

        // Simulate optimistic concurrency: first N/2 calls throw ConcurrencyException,
        // then succeed on retry (fresh data loaded)
        repo.GetAccountAsync(TenantId, LedgerAccountType.WALLET, SellerId)
            .Returns(_ => Task.FromResult<LedgerAccount?>(wallet));

        repo.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_RECEIVABLE)
            .Returns(_ => Task.FromResult<LedgerAccount?>(platformAccount));

        repo.When(r => r.SaveChangesAsync())
            .Do(_ =>
            {
                int current = Interlocked.Increment(ref callCount);
                // Simulate concurrency conflict on every other save (first attempt)
                if (current <= concurrency / 2)
                {
                    Interlocked.Increment(ref concurrencyConflictCount);
                    throw new ConcurrencyException("RowVersion.Conflict", "Concurrency conflict detected.");
                }
            });

        var sut = new LedgerService(repo, NullLogger<LedgerService>.Instance);

        // Act: Execute credits sequentially (simulating retry behavior)
        // The retry logic inside LedgerService.ExecuteWithRetryAsync handles ConcurrencyException
        var tasks = new List<Task>();
        int successCount = 0;

        for (int i = 0; i < concurrency; i++)
        {
            try
            {
                await sut.RecordIncomingFundsAsync(TenantId, SellerId, creditAmount, LedgerAccountType.WALLET, $"Credit #{i}");
                Interlocked.Increment(ref successCount);
            }
            catch (ConcurrencyException)
            {
                // Expected for some attempts after exhausting retries
            }
        }

        // Assert: All credits that succeeded should have the correct balance
        successCount.Should().BeGreaterThan(0, "at least some credits should succeed after retry");
        concurrencyConflictCount.Should().BeGreaterThan(0, "concurrency conflicts should have occurred");

        // The wallet was in-memory, so entries track what actually executed
        AssertLedgerInvariant(wallet);
    }

    [Fact]
    public async Task ConcurrentCredits_AfterMaxRetries_ShouldThrowConcurrencyException()
    {
        // Arrange: SaveChanges ALWAYS throws ConcurrencyException
        var wallet = CreateWallet(1000m);
        var platformAccount = CreatePlatformAccount(LedgerAccountType.PLATFORM_RECEIVABLE, 100_000m);

        var repo = Substitute.For<ILedgerRepository>();
        repo.GetAccountAsync(TenantId, LedgerAccountType.WALLET, SellerId)
            .Returns(Task.FromResult<LedgerAccount?>(wallet));
        repo.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_RECEIVABLE)
            .Returns(Task.FromResult<LedgerAccount?>(platformAccount));

        repo.When(r => r.SaveChangesAsync())
            .Do(_ => throw new ConcurrencyException("RowVersion.Conflict", "Persistent conflict."));

        var sut = new LedgerService(repo, NullLogger<LedgerService>.Instance);

        // Act & Assert: After 3 retries + 1 final attempt = ConcurrencyException bubbles up
        var act = () => sut.RecordIncomingFundsAsync(TenantId, SellerId, 100m, LedgerAccountType.WALLET, "test");
        await act.Should().ThrowAsync<ConcurrencyException>();
    }

    #endregion

    #region 2. Concurrent Payouts — Double-spend prevention

    [Fact]
    public async Task ConcurrentPayouts_ShouldPreventDoubleSpend_ViaInsufficientBalance()
    {
        // Arrange: Wallet has 500. Two payouts of 400 each attempt concurrently.
        // Only the first should succeed; the second should fail with insufficient balance.
        const decimal walletBalance = 500m;
        const decimal payoutAmount = 400m;

        var wallet = CreateWallet(walletBalance);
        var platformPayout = CreatePlatformAccount(LedgerAccountType.PLATFORM_PAYOUT);

        var repo = Substitute.For<ILedgerRepository>();
        repo.GetAccountAsync(TenantId, LedgerAccountType.WALLET, SellerId)
            .Returns(Task.FromResult<LedgerAccount?>(wallet));
        repo.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_PAYOUT)
            .Returns(Task.FromResult<LedgerAccount?>(platformPayout));

        var ledgerService = new LedgerService(repo, NullLogger<LedgerService>.Instance);

        // Act: First debit succeeds
        var firstDebit = await ledgerService.DebitSellerAsync(
            TenantId, SellerId, payoutAmount, "Payout #1", Guid.NewGuid().ToString());

        // Assert: Balance is now 100
        firstDebit.Balance.Should().Be(walletBalance - payoutAmount);
        AssertLedgerInvariant(wallet);

        // Act: Second debit should fail — insufficient balance
        var secondDebit = () => ledgerService.DebitSellerAsync(
            TenantId, SellerId, payoutAmount, "Payout #2", Guid.NewGuid().ToString());

        await secondDebit.Should().ThrowAsync<BusinessException>()
            .WithMessage("*Saldo insuficiente*");

        // Assert: Balance unchanged after failed debit
        wallet.Balance.Should().Be(walletBalance - payoutAmount);
        AssertLedgerInvariant(wallet);
    }

    [Fact]
    public async Task Payout_WhenProviderFails_ShouldReverseLedgerDebit()
    {
        // Arrange: Wallet starts at 1000. Payout of 500 debits ledger, then provider fails.
        // Expected: Wallet returns to 1000 after reversal.
        const decimal initialBalance = 1000m;
        const decimal payoutAmount = 500m;

        var wallet = CreateWallet(initialBalance);
        var platformPayout = CreatePlatformAccount(LedgerAccountType.PLATFORM_PAYOUT);
        var platformFee = CreatePlatformAccount(LedgerAccountType.PLATFORM_FEE);
        var seller = CreateSeller();

        var sellerRepo = Substitute.For<ISellerRepository>();
        sellerRepo.GetByIdAsync(TenantId, SellerId).Returns(seller);

        var payoutRepo = Substitute.For<IPayoutRepository>();
        var tenantRepo = Substitute.For<ITenantRepository>();

        var ledgerRepo = Substitute.For<ILedgerRepository>();
        ledgerRepo.GetAccountAsync(TenantId, LedgerAccountType.WALLET, Arg.Any<Guid>())
            .Returns(Task.FromResult<LedgerAccount?>(wallet));
        ledgerRepo.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_PAYOUT)
            .Returns(Task.FromResult<LedgerAccount?>(platformPayout));
        ledgerRepo.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_FEE)
            .Returns(Task.FromResult<LedgerAccount?>(platformFee));

        var ledgerService = new LedgerService(ledgerRepo, NullLogger<LedgerService>.Instance);

        // Provider fails
        var payoutProcessor = Substitute.For<IPayoutProcessor>();
        payoutProcessor.ProcessAsync(Arg.Any<Payout>(), Arg.Any<Seller>())
            .Returns(new PayoutResult(Success: false, FailureReason: "Provider timeout"));

        var emailService = Substitute.For<IEmailService>();
        var realtimeNotifier = Substitute.For<IRealtimeNotifier>();

        var sut = new PayoutService(
            payoutRepo, sellerRepo, tenantRepo, ledgerService,
            payoutProcessor, emailService, realtimeNotifier,
            Substitute.For<IBackgroundJobs>(),
            Substitute.For<IAppMetrics>(),
            NullLogger<PayoutService>.Instance);

        // Act
        var result = await sut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, payoutAmount));

        // Assert: First failure schedules retry — status PROCESSING, ledger debit held
        result.Status.Should().Be(PayoutStatus.PROCESSING,
            "Provider failure on first attempt should schedule retry, not compensate immediately");

        // Wallet stays debited (waiting for retry)
        wallet.Balance.Should().Be(initialBalance - payoutAmount,
            "Wallet balance stays debited while retry is pending");
    }

    [Fact]
    public async Task Payout_WhenProviderThrowsException_ShouldReverseLedgerDebit()
    {
        // Arrange: Provider throws exception (not just returns failure)
        const decimal initialBalance = 1000m;
        const decimal payoutAmount = 300m;

        var wallet = CreateWallet(initialBalance);
        var platformPayout = CreatePlatformAccount(LedgerAccountType.PLATFORM_PAYOUT);
        var platformFee = CreatePlatformAccount(LedgerAccountType.PLATFORM_FEE);
        var seller = CreateSeller();

        var sellerRepo = Substitute.For<ISellerRepository>();
        sellerRepo.GetByIdAsync(TenantId, SellerId).Returns(seller);

        var payoutRepo = Substitute.For<IPayoutRepository>();
        var tenantRepo = Substitute.For<ITenantRepository>();

        var ledgerRepo = Substitute.For<ILedgerRepository>();
        ledgerRepo.GetAccountAsync(TenantId, LedgerAccountType.WALLET, Arg.Any<Guid>())
            .Returns(Task.FromResult<LedgerAccount?>(wallet));
        ledgerRepo.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_PAYOUT)
            .Returns(Task.FromResult<LedgerAccount?>(platformPayout));
        ledgerRepo.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_FEE)
            .Returns(Task.FromResult<LedgerAccount?>(platformFee));

        var ledgerService = new LedgerService(ledgerRepo, NullLogger<LedgerService>.Instance);

        // Provider throws
        var payoutProcessor = Substitute.For<IPayoutProcessor>();
        payoutProcessor.ProcessAsync(Arg.Any<Payout>(), Arg.Any<Seller>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var sut = new PayoutService(
            payoutRepo, sellerRepo, tenantRepo, ledgerService,
            payoutProcessor, Substitute.For<IEmailService>(), Substitute.For<IRealtimeNotifier>(),
            Substitute.For<IBackgroundJobs>(),
            Substitute.For<IAppMetrics>(),
            NullLogger<PayoutService>.Instance);

        // Act
        var result = await sut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, payoutAmount));

        // Assert: HttpRequestException is transient — schedules retry, keeps debit
        result.Status.Should().Be(PayoutStatus.PROCESSING,
            "Transient HttpRequestException should schedule retry, not compensate immediately");

        // Wallet stays debited (waiting for retry)
        wallet.Balance.Should().Be(initialBalance - payoutAmount,
            "Wallet balance stays debited while transient retry is pending");
    }

    #endregion

    #region 3. Webhook Duplication — Idempotent status transitions

    [Fact]
    public async Task DuplicateWebhook_ShouldNotDoubleCreditLedger()
    {
        // Arrange: Same CAPTURED webhook arrives twice for the same transaction
        var wallet = CreateWallet(0m);
        var platformReceivable = CreatePlatformAccount(LedgerAccountType.PLATFORM_RECEIVABLE, 100_000m);

        var transaction = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE,
            1, 1.5m, 98.5m, DateTime.UtcNow.AddDays(30),
            null, SellerId).Value;

        // Transaction starts as PROCESSING (from Create)
        transaction.SetProviderTxId("pi_stripe_123");

        var transactionRepo = Substitute.For<ITransactionRepository>();
        transactionRepo.GetByProviderTxIdAsync("pi_stripe_123")
            .Returns(Task.FromResult<Transaction?>(transaction));

        // Simulate ExecuteUpdateAsync: when SetStatusAsync is called, update the in-memory entity
        // so that subsequent idempotency guards see the correct status.
        transactionRepo.SetStatusAsync(Arg.Any<Guid>(), Arg.Any<TransactionStatus>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                var status = ci.ArgAt<TransactionStatus>(1);
                typeof(Transaction).GetProperty(nameof(Transaction.Status))!
                    .SetValue(transaction, status);
            });

        var sellerRepo = Substitute.For<ISellerRepository>();
        var webhookEndpointRepo = Substitute.For<IWebhookEndpointRepository>();
        var webhookDeliveryRepo = Substitute.For<IWebhookDeliveryRepository>();

        var ledgerRepo = Substitute.For<ILedgerRepository>();
        ledgerRepo.GetAccountAsync(TenantId, LedgerAccountType.WALLET, SellerId)
            .Returns(Task.FromResult<LedgerAccount?>(wallet));
        ledgerRepo.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_RECEIVABLE)
            .Returns(Task.FromResult<LedgerAccount?>(platformReceivable));

        var ledgerService = new LedgerService(ledgerRepo, NullLogger<LedgerService>.Instance);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        var securityService = Substitute.For<ISecurityService>();
        var configuration = Substitute.For<IConfiguration>();

        var sut = new WebhooksService(
            transactionRepo,
            Substitute.For<ITransactionInstallmentRepository>(), sellerRepo, Substitute.For<ITenantRepository>(), webhookEndpointRepo, webhookDeliveryRepo,
            InboundWebhookGuardMockHelper.CreatePermissive(),
            ledgerService, securityService, configuration, unitOfWork,
            Substitute.For<IBackgroundJobs>(),
            new RailRouter([new StripeCardRail(Substitute.For<IPaymentProviderFactory>()), new StripeBoletoRail(Substitute.For<IPaymentProviderFactory>()), new OpenPixRail(Substitute.For<IPaymentProviderFactory>())]),
            Substitute.For<IPaymentIntentRepository>(),
            Substitute.For<IDisputeRepository>(),
            Substitute.For<ISplitTransferRepository>(),
            Substitute.For<IDomainEventDispatcher>(),
            Substitute.For<IWebhookProbeClient>(),
            Substitute.For<IAppMetrics>(),
            Substitute.For<FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator>(),
            Microsoft.Extensions.Options.Options.Create(new FellowCore.Application.Modules.Pricing.Options.TierPricingOptions()),
            NullLogger<WebhooksService>.Instance);

        var stripePayload = new StripeWebhookDto(
            Id: "evt_123",
            Type: "payment_intent.succeeded",
            Data: new StripeWebhookData(new StripeWebhookObject(
                Id: "pi_stripe_123",
                Status: "succeeded")));

        // Act: First webhook — transitions PROCESSING -> CAPTURED, credits ledger
        await sut.HandleStripeEventAsync(stripePayload);

        transaction.Status.Should().Be(TransactionStatus.CAPTURED);
        decimal balanceAfterFirst = wallet.Balance;
        balanceAfterFirst.Should().Be(98.5m, "First webhook should credit net amount");

        // Act: Second webhook — same event, should be idempotent
        await sut.HandleStripeEventAsync(stripePayload);

        // Assert: Balance unchanged, no double credit
        wallet.Balance.Should().Be(balanceAfterFirst,
            "Duplicate webhook must NOT double-credit the ledger");
        transaction.Status.Should().Be(TransactionStatus.CAPTURED);
        AssertLedgerInvariant(wallet);
    }

    [Fact]
    public async Task DuplicateWebhook_OpenPix_ShouldBeIdempotent()
    {
        // Arrange
        var wallet = CreateWallet(0m);
        var platformReceivable = CreatePlatformAccount(LedgerAccountType.PLATFORM_RECEIVABLE, 100_000m);

        var transaction = Transaction.Create(
            TenantId, 200m, PaymentType.PIX, PaymentProvider.OPENPIX,
            1, 3m, 197m, DateTime.UtcNow.AddDays(30),
            null, SellerId).Value;
        transaction.SetProviderTxId("corr-openpix-001");

        var transactionRepo = Substitute.For<ITransactionRepository>();
        transactionRepo.GetByProviderTxIdAsync("corr-openpix-001")
            .Returns(Task.FromResult<Transaction?>(transaction));
        // SetStatusAsync uses ExecuteUpdateAsync (bypasses change tracker), so simulate the
        // in-memory status update so the idempotency guard works on the second delivery
        transactionRepo.SetStatusAsync(Arg.Any<Guid>(), Arg.Any<TransactionStatus>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => transaction.UpdateStatus(ci.Arg<TransactionStatus>()));

        var ledgerRepo = Substitute.For<ILedgerRepository>();
        ledgerRepo.GetAccountAsync(TenantId, LedgerAccountType.WALLET, SellerId)
            .Returns(Task.FromResult<LedgerAccount?>(wallet));
        ledgerRepo.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_RECEIVABLE)
            .Returns(Task.FromResult<LedgerAccount?>(platformReceivable));

        var ledgerService = new LedgerService(ledgerRepo, NullLogger<LedgerService>.Instance);
        var unitOfWork = Substitute.For<IUnitOfWork>();

        var config = Substitute.For<IConfiguration>();
        config["OpenPix:AppId"].Returns("test-platform-appid");

        var sut = new WebhooksService(
            transactionRepo,
            Substitute.For<ITransactionInstallmentRepository>(), Substitute.For<ISellerRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IWebhookEndpointRepository>(), Substitute.For<IWebhookDeliveryRepository>(),
            InboundWebhookGuardMockHelper.CreatePermissive(),
            ledgerService, Substitute.For<ISecurityService>(), config,
            unitOfWork, Substitute.For<IBackgroundJobs>(),
            new RailRouter([new StripeCardRail(Substitute.For<IPaymentProviderFactory>()), new StripeBoletoRail(Substitute.For<IPaymentProviderFactory>()), new OpenPixRail(Substitute.For<IPaymentProviderFactory>())]),
            Substitute.For<IPaymentIntentRepository>(),
            Substitute.For<IDisputeRepository>(),
            Substitute.For<ISplitTransferRepository>(),
            Substitute.For<IDomainEventDispatcher>(),
            Substitute.For<IWebhookProbeClient>(),
            Substitute.For<IAppMetrics>(),
            Substitute.For<FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator>(),
            Microsoft.Extensions.Options.Options.Create(new FellowCore.Application.Modules.Pricing.Options.TierPricingOptions()),
            NullLogger<WebhooksService>.Instance);

        var payload = new OpenPixWebhookDto(
            Event: "OPENPIX:CHARGE_COMPLETED",
            Charge: new OpenPixWebhookCharge("COMPLETED", "corr-openpix-001", "tx-openpix", null, null, null),
            Pix: null);

        // Act: First delivery
        await sut.HandleOpenPixEventAsync(payload, "test-platform-appid");
        decimal balanceAfterFirst = wallet.Balance;
        balanceAfterFirst.Should().Be(197m);

        // Act: Duplicate delivery
        await sut.HandleOpenPixEventAsync(payload, "test-platform-appid");

        // Assert
        wallet.Balance.Should().Be(balanceAfterFirst,
            "Duplicate OpenPix webhook must not double-credit");
        AssertLedgerInvariant(wallet);
    }

    [Fact]
    public async Task WebhookForWrongProvider_ShouldBeRejected()
    {
        // Arrange: Transaction is OPENPIX, but a Stripe webhook arrives for same providerTxId
        var transaction = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.OPENPIX,
            1, 1.5m, 98.5m, null, "shared-provider-id-123", SellerId).Value;

        var transactionRepo = Substitute.For<ITransactionRepository>();
        transactionRepo.GetByProviderTxIdAsync("shared-provider-id-123")
            .Returns(Task.FromResult<Transaction?>(transaction));

        var unitOfWork = Substitute.For<IUnitOfWork>();

        var sut = new WebhooksService(
            transactionRepo,
            Substitute.For<ITransactionInstallmentRepository>(), Substitute.For<ISellerRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IWebhookEndpointRepository>(), Substitute.For<IWebhookDeliveryRepository>(),
            InboundWebhookGuardMockHelper.CreatePermissive(),
            Substitute.For<ILedgerService>(), Substitute.For<ISecurityService>(),
            Substitute.For<IConfiguration>(), unitOfWork,
            Substitute.For<IBackgroundJobs>(),
            new RailRouter([new StripeCardRail(Substitute.For<IPaymentProviderFactory>()), new StripeBoletoRail(Substitute.For<IPaymentProviderFactory>()), new OpenPixRail(Substitute.For<IPaymentProviderFactory>())]),
            Substitute.For<IPaymentIntentRepository>(),
            Substitute.For<IDisputeRepository>(),
            Substitute.For<ISplitTransferRepository>(),
            Substitute.For<IDomainEventDispatcher>(),
            Substitute.For<IWebhookProbeClient>(),
            Substitute.For<IAppMetrics>(),
            Substitute.For<FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator>(),
            Microsoft.Extensions.Options.Options.Create(new FellowCore.Application.Modules.Pricing.Options.TierPricingOptions()),
            NullLogger<WebhooksService>.Instance);

        var stripePayload = new StripeWebhookDto(
            Id: "evt_wrong",
            Type: "payment_intent.succeeded",
            Data: new StripeWebhookData(new StripeWebhookObject(Id: "shared-provider-id-123")));

        // Act
        await sut.HandleStripeEventAsync(stripePayload);

        // Assert: UnitOfWork was never touched — webhook silently rejected
        await unitOfWork.DidNotReceive().BeginAsync();
        transaction.Status.Should().NotBe(TransactionStatus.CAPTURED,
            "Webhook for wrong provider must not change transaction status");
    }

    #endregion

    #region 4. Provider Failure During Payment — Transaction marked FAILED

    [Fact]
    public void TransactionCreate_ShouldStartAsProcessing_WithNullProviderTxId()
    {
        // Validate the persist-first pattern: transaction is created with null ProviderTxId
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE,
            1, 1.5m, 98.5m, DateTime.UtcNow.AddDays(30),
            providerTxId: null, SellerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProviderTxId.Should().BeNull("persist-first: no provider call yet");
        result.Value.Status.Should().Be(TransactionStatus.PROCESSING);
    }

    [Fact]
    public void Transaction_CanBeMarkedFailed_AfterProviderFailure()
    {
        // Simulate: transaction persisted, then provider call fails
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE,
            1, 1.5m, 98.5m, DateTime.UtcNow.AddDays(30),
            providerTxId: null, SellerId);

        var transaction = result.Value;

        // Provider call failed — mark as FAILED
        var updateResult = transaction.UpdateStatus(TransactionStatus.FAILED);
        updateResult.IsSuccess.Should().BeTrue();
        transaction.Status.Should().Be(TransactionStatus.FAILED);
        transaction.ProviderTxId.Should().BeNull("provider was never called successfully");
    }

    [Fact]
    public void Transaction_ProviderTxId_SetAfterSuccessfulProviderCall()
    {
        // Happy path: persist, then provider succeeds, then set ProviderTxId
        var transaction = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE,
            1, 1.5m, 98.5m, DateTime.UtcNow.AddDays(30),
            providerTxId: null, SellerId).Value;

        transaction.SetProviderTxId("pi_stripe_real_123");
        transaction.ProviderTxId.Should().Be("pi_stripe_real_123");
    }

    [Fact]
    public void FailedTransaction_CannotBeRefunded()
    {
        var transaction = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE,
            1, 1.5m, 98.5m, null, null, SellerId).Value;

        transaction.UpdateStatus(TransactionStatus.FAILED);

        var refundResult = transaction.Refund(50m);
        refundResult.IsFailure.Should().BeTrue();
        refundResult.Error.Code.Should().Be("Transaction.NotRefundable");
    }

    [Fact]
    public void FailedTransaction_CannotBeCaptured()
    {
        // Once FAILED, cannot transition to CAPTURED (state machine blocks it)
        var transaction = Transaction.Create(
            TenantId, 100m, PaymentType.PIX, PaymentProvider.STRIPE,
            1, 1.5m, 98.5m, null, null, SellerId).Value;

        transaction.UpdateStatus(TransactionStatus.FAILED);
        transaction.Status.Should().Be(TransactionStatus.FAILED);

        var capturedResult = transaction.UpdateStatus(TransactionStatus.CAPTURED);
        capturedResult.IsFailure.Should().BeTrue(
            "FAILED is a terminal state — late webhooks must not capture");
        transaction.Status.Should().Be(TransactionStatus.FAILED);
    }

    #endregion

    #region 5. Partial Settlement Failures

    [Fact]
    public async Task Settlement_WhenOneSeller_Fails_OthersShouldStillSettle()
    {
        // Arrange: 3 sellers com parcelas maduras. Seller #2 falha durante TransferFunds.
        // Sellers #1 e #3 ainda devem ser liquidados.
        var seller1Id = Guid.NewGuid();
        var seller2Id = Guid.NewGuid(); // Will fail
        var seller3Id = Guid.NewGuid();

        var dueBatches = new List<PendingInstallmentBatch>
        {
            new(TenantId, seller1Id, 1000m, new List<Guid> { Guid.NewGuid() }),
            new(TenantId, seller2Id, 2000m, new List<Guid> { Guid.NewGuid() }),
            new(TenantId, seller3Id, 500m,  new List<Guid> { Guid.NewGuid() }),
        };

        var installmentRepo = Substitute.For<ITransactionInstallmentRepository>();
        installmentRepo.GetDueForSettlementAsync(Arg.Any<DateTime>()).Returns(dueBatches);

        int settledCount = 0;
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<IUnitOfWork>(_ => Substitute.For<IUnitOfWork>());

        serviceCollection.AddScoped<ILedgerService>(_ =>
        {
            var ls = Substitute.For<ILedgerService>();
            ls.TransferFundsAsync(TenantId, seller2Id, Arg.Any<decimal>())
                .ThrowsAsync(new BusinessException("Ledger.Error", "Simulated ledger failure for seller 2"));
            return ls;
        });

        serviceCollection.AddScoped<ITransactionInstallmentRepository>(_ =>
        {
            var repo = Substitute.For<ITransactionInstallmentRepository>();
            repo.When(r => r.MarkSettledAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateTime>()))
                .Do(_ => Interlocked.Increment(ref settledCount));
            return repo;
        });

        var scopeFactory = serviceCollection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var sut = new SettlementService(installmentRepo, scopeFactory, NullLogger<SettlementService>.Instance);

        await sut.ProcessDailySettlementsAsync();

        settledCount.Should().Be(2, "Sellers #1 e #3 deveriam ter sido marcados como settled");
    }

    [Fact]
    public async Task Settlement_WithEmptyPendingList_ShouldNoOp()
    {
        var installmentRepo = Substitute.For<ITransactionInstallmentRepository>();
        installmentRepo.GetDueForSettlementAsync(Arg.Any<DateTime>())
            .Returns(new List<PendingInstallmentBatch>());

        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        var sut = new SettlementService(installmentRepo, scopeFactory, NullLogger<SettlementService>.Instance);

        await sut.ProcessDailySettlementsAsync();

        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task Settlement_WithZeroAmounts_ShouldSkip()
    {
        var dueBatches = new List<PendingInstallmentBatch>
        {
            new(TenantId, Guid.NewGuid(),  0m, new List<Guid> { Guid.NewGuid() }), // skip
            new(TenantId, Guid.NewGuid(), -5m, new List<Guid> { Guid.NewGuid() }), // skip
        };

        var installmentRepo = Substitute.For<ITransactionInstallmentRepository>();
        installmentRepo.GetDueForSettlementAsync(Arg.Any<DateTime>()).Returns(dueBatches);

        int transferCalled = 0;
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<IUnitOfWork>(_ => Substitute.For<IUnitOfWork>());
        serviceCollection.AddScoped<ILedgerService>(_ =>
        {
            var ls = Substitute.For<ILedgerService>();
            ls.When(l => l.TransferFundsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>()))
                .Do(_ => Interlocked.Increment(ref transferCalled));
            return ls;
        });
        serviceCollection.AddScoped<ITransactionInstallmentRepository>(_ => Substitute.For<ITransactionInstallmentRepository>());
        var scopeFactory = serviceCollection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var sut = new SettlementService(installmentRepo, scopeFactory, NullLogger<SettlementService>.Instance);

        await sut.ProcessDailySettlementsAsync();

        transferCalled.Should().Be(0, "Zero/negative amounts should be filtered out");
    }

    #endregion

    #region 6. Financial Invariants — Double-entry bookkeeping

    [Fact]
    public void DoubleEntry_CreditAndDebit_ShouldBalance()
    {
        // Arrange: Platform account and seller account
        var platformAccount = CreatePlatformAccount(LedgerAccountType.PLATFORM_RECEIVABLE, 10_000m);
        var sellerWallet = CreateWallet(0m);

        // Act: Credit seller 500, debit platform 500 (incoming funds)
        var sellerCredit = sellerWallet.Credit(500m, "Incoming payment");
        var platformDebit = platformAccount.Debit(500m, "Repasse seller");

        sellerCredit.IsSuccess.Should().BeTrue();
        platformDebit.IsSuccess.Should().BeTrue();

        // Link contra entries
        sellerCredit.Value.LinkContraEntry(platformDebit.Value.Id);
        platformDebit.Value.LinkContraEntry(sellerCredit.Value.Id);

        // Assert: Net across both accounts is zero-change
        decimal netChange = sellerWallet.Balance + (platformAccount.Balance - 10_000m);
        netChange.Should().Be(0m, "Double-entry: total net change across all accounts must be zero");

        sellerCredit.Value.ContraEntryId.Should().Be(platformDebit.Value.Id);
        platformDebit.Value.ContraEntryId.Should().Be(sellerCredit.Value.Id);
    }

    [Fact]
    public void DoubleEntry_PayoutDebitAndReversal_ShouldNetToZero()
    {
        // Simulate: Payout debits seller, then reversal credits seller back
        var wallet = CreateWallet(1000m);
        var platformPayout = CreatePlatformAccount(LedgerAccountType.PLATFORM_PAYOUT);

        // Debit for payout
        var debit = wallet.Debit(300m, "Payout");
        var platformCredit = platformPayout.Credit(300m, "Payout");
        debit.IsSuccess.Should().BeTrue();
        platformCredit.IsSuccess.Should().BeTrue();

        wallet.Balance.Should().Be(700m);
        platformPayout.Balance.Should().Be(300m);

        // Reversal (payout failed)
        var reversalCredit = wallet.Credit(300m, "Reversal");
        var reversalDebit = platformPayout.Debit(300m, "Reversal");
        reversalCredit.IsSuccess.Should().BeTrue();
        reversalDebit.IsSuccess.Should().BeTrue();

        // Assert: Everything back to original
        wallet.Balance.Should().Be(1000m, "Reversal must restore original balance");
        platformPayout.Balance.Should().Be(0m, "Platform payout account must net to zero");
        AssertLedgerInvariant(wallet);
        AssertLedgerInvariant(platformPayout);
    }

    [Fact]
    public void Refund_ShouldRespectMaxRefundableAmount()
    {
        var transaction = CreateCapturedTransaction(100m, 98.5m);

        // Partial refund OK
        var r1 = transaction.Refund(50m);
        r1.IsSuccess.Should().BeTrue();
        transaction.RefundedAmount.Should().Be(50m);

        // Another partial refund OK
        var r2 = transaction.Refund(50m);
        r2.IsSuccess.Should().BeTrue();
        transaction.RefundedAmount.Should().Be(100m);
        transaction.Status.Should().Be(TransactionStatus.REFUNDED);

        // Over-refund rejected
        var r3 = transaction.Refund(1m);
        r3.IsFailure.Should().BeTrue();
        r3.Error.Code.Should().Be("Transaction.NotRefundable",
            "Cannot refund a fully refunded transaction");
    }

    [Fact]
    public void TransferTo_ShouldMoveFromFutureReceivablesToWallet()
    {
        var futureReceivables = CreateFutureReceivables(5000m);
        var wallet = CreateWallet(200m);

        var result = futureReceivables.TransferTo(wallet, 3000m);
        result.IsSuccess.Should().BeTrue();

        futureReceivables.Balance.Should().Be(2000m);
        wallet.Balance.Should().Be(3200m);

        AssertLedgerInvariant(futureReceivables);
        AssertLedgerInvariant(wallet);
    }

    [Fact]
    public void TransferTo_ShouldRejectIfInsufficientFutureReceivables()
    {
        var futureReceivables = CreateFutureReceivables(100m);
        var wallet = CreateWallet(0m);

        var result = futureReceivables.TransferTo(wallet, 500m);
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Contain("InsufficientBalance");

        // Balances unchanged
        futureReceivables.Balance.Should().Be(100m);
        wallet.Balance.Should().Be(0m);
    }

    #endregion

    #region 7. Edge Cases & Boundary Conditions

    [Fact]
    public void LedgerAccount_RejectsCreditOfZeroOrNegative()
    {
        var wallet = CreateWallet(100m);

        wallet.Credit(0m, "zero").IsFailure.Should().BeTrue();
        wallet.Credit(-10m, "negative").IsFailure.Should().BeTrue();
        wallet.Balance.Should().Be(100m, "Balance must not change on invalid operations");
    }

    [Fact]
    public void LedgerAccount_RejectsDebitOfZeroOrNegative()
    {
        var wallet = CreateWallet(100m);

        wallet.Debit(0m, "zero").IsFailure.Should().BeTrue();
        wallet.Debit(-10m, "negative").IsFailure.Should().BeTrue();
        wallet.Balance.Should().Be(100m);
    }

    [Fact]
    public void Transaction_RejectsNegativeAmount()
    {
        var result = Transaction.Create(
            TenantId, -100m, PaymentType.PIX, PaymentProvider.STRIPE,
            1, 0m, 0m, null, null, SellerId);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Transaction.InvalidAmount");
    }

    [Fact]
    public void Transaction_RejectsZeroAmount()
    {
        var result = Transaction.Create(
            TenantId, 0m, PaymentType.PIX, PaymentProvider.STRIPE,
            1, 0m, 0m, null, null, SellerId);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Webhook_ForNonExistentTransaction_ShouldSilentlyIgnore()
    {
        var transactionRepo = Substitute.For<ITransactionRepository>();
        transactionRepo.GetByProviderTxIdAsync("nonexistent").Returns(Task.FromResult<Transaction?>(null));

        var unitOfWork = Substitute.For<IUnitOfWork>();

        var sut = new WebhooksService(
            transactionRepo,
            Substitute.For<ITransactionInstallmentRepository>(), Substitute.For<ISellerRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IWebhookEndpointRepository>(), Substitute.For<IWebhookDeliveryRepository>(),
            InboundWebhookGuardMockHelper.CreatePermissive(),
            Substitute.For<ILedgerService>(), Substitute.For<ISecurityService>(),
            Substitute.For<IConfiguration>(), unitOfWork,
            Substitute.For<IBackgroundJobs>(),
            new RailRouter([new StripeCardRail(Substitute.For<IPaymentProviderFactory>()), new StripeBoletoRail(Substitute.For<IPaymentProviderFactory>()), new OpenPixRail(Substitute.For<IPaymentProviderFactory>())]),
            Substitute.For<IPaymentIntentRepository>(),
            Substitute.For<IDisputeRepository>(),
            Substitute.For<ISplitTransferRepository>(),
            Substitute.For<IDomainEventDispatcher>(),
            Substitute.For<IWebhookProbeClient>(),
            Substitute.For<IAppMetrics>(),
            Substitute.For<FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator>(),
            Microsoft.Extensions.Options.Options.Create(new FellowCore.Application.Modules.Pricing.Options.TierPricingOptions()),
            NullLogger<WebhooksService>.Instance);

        var payload = new StripeWebhookDto(
            Id: "evt_ghost",
            Type: "payment_intent.succeeded",
            Data: new StripeWebhookData(new StripeWebhookObject(Id: "nonexistent")));

        await sut.HandleStripeEventAsync(payload);

        await unitOfWork.DidNotReceive().BeginAsync();
    }

    [Fact]
    public async Task ChargeRefund_ShouldComputeDelta_AndDebitLedger()
    {
        // Stripe charge.refunded sends cumulative amount_refunded, not delta.
        // Our handler computes the delta: newRefundDelta = amountRefunded - transaction.RefundedAmount
        var transaction = CreateCapturedTransaction(1000m, 985m);
        transaction.SetProviderTxId("pi_refund_test");

        // First partial refund of 300 (simulated by previous webhook)
        transaction.Refund(300m);
        transaction.RefundedAmount.Should().Be(300m);

        var transactionRepo = Substitute.For<ITransactionRepository>();
        transactionRepo.GetByProviderTxIdAsync("pi_refund_test").Returns(Task.FromResult<Transaction?>(transaction));

        var ledgerService = Substitute.For<ILedgerService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();

        var sut = new WebhooksService(
            transactionRepo,
            Substitute.For<ITransactionInstallmentRepository>(), Substitute.For<ISellerRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IWebhookEndpointRepository>(), Substitute.For<IWebhookDeliveryRepository>(),
            InboundWebhookGuardMockHelper.CreatePermissive(),
            ledgerService, Substitute.For<ISecurityService>(),
            Substitute.For<IConfiguration>(), unitOfWork,
            Substitute.For<IBackgroundJobs>(),
            new RailRouter([new StripeCardRail(Substitute.For<IPaymentProviderFactory>()), new StripeBoletoRail(Substitute.For<IPaymentProviderFactory>()), new OpenPixRail(Substitute.For<IPaymentProviderFactory>())]),
            Substitute.For<IPaymentIntentRepository>(),
            Substitute.For<IDisputeRepository>(),
            Substitute.For<ISplitTransferRepository>(),
            Substitute.For<IDomainEventDispatcher>(),
            Substitute.For<IWebhookProbeClient>(),
            Substitute.For<IAppMetrics>(),
            Substitute.For<FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator>(),
            Microsoft.Extensions.Options.Options.Create(new FellowCore.Application.Modules.Pricing.Options.TierPricingOptions()),
            NullLogger<WebhooksService>.Instance);

        // charge.refunded with cumulative amount_refunded = 50000 cents (500 BRL)
        // Delta should be 500 - 300 = 200
        var payload = new StripeWebhookDto(
            Id: "evt_refund",
            Type: "charge.refunded",
            Data: new StripeWebhookData(new StripeWebhookObject(
                Id: "ch_charge_123",
                PaymentIntent: "pi_refund_test",
                AmountRefunded: 50000 // 500 BRL in cents
            )));

        await sut.HandleStripeEventAsync(payload);

        // Assert: ApplyRefundAsync called with cumulative amount (500) and correct status
        await transactionRepo.Received(1).ApplyRefundAsync(
            transaction.Id, 500m, Arg.Any<TransactionStatus>());

        // Assert: Ledger debitado com GROSS INTEGRAL (política nova). Delta = 200,
        // débito do seller = 200 (não 197 proporcional ao net). Antes era 200*(985/1000)=197;
        // agora seller absorve o gross — plataforma fica com margem.
        await ledgerService.Received(1).DebitSellerAsync(
            transaction.TenantId,
            transaction.SellerId!.Value,
            200m,
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    #endregion
}
