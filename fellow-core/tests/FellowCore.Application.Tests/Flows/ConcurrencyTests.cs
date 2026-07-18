using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Tests.Helpers;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Rails;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using FellowCore.Application.Modules.Webhooks.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Flows;

/// <summary>
/// T16: Concurrent scenario tests.
/// Tests double-capture prevention via PaymentIntent and race condition handling.
/// </summary>
public class ConcurrencyTests
{
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IWebhookEndpointRepository _webhookEndpointRepository = Substitute.For<IWebhookEndpointRepository>();
    private readonly IWebhookDeliveryRepository _webhookDeliveryRepository = Substitute.For<IWebhookDeliveryRepository>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly ISecurityService _securityService = Substitute.For<ISecurityService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IBackgroundJobs _backgroundJobs = Substitute.For<IBackgroundJobs>();
    private readonly IRailRouter _railRouter = Substitute.For<IRailRouter>();
    private readonly IPaymentIntentRepository _paymentIntentRepository = Substitute.For<IPaymentIntentRepository>();
    private readonly IDisputeRepository _disputeRepository = Substitute.For<IDisputeRepository>();
    private readonly IDomainEventDispatcher _domainEventDispatcher = Substitute.For<IDomainEventDispatcher>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly WebhooksService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    public ConcurrencyTests()
    {
        _sut = new WebhooksService(
            _transactionRepository,
            Substitute.For<ITransactionInstallmentRepository>(), _sellerRepository, _tenantRepository,
            _webhookEndpointRepository, _webhookDeliveryRepository,
            InboundWebhookGuardMockHelper.CreatePermissive(),
            _ledgerService, _securityService, _configuration, _unitOfWork,
            _backgroundJobs, _railRouter, _paymentIntentRepository,
            _disputeRepository, Substitute.For<ISplitTransferRepository>(),
            _domainEventDispatcher,
            Substitute.For<IWebhookProbeClient>(),
            Substitute.For<IAppMetrics>(),
            Substitute.For<FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator>(),
            Microsoft.Extensions.Options.Options.Create(new FellowCore.Application.Modules.Pricing.Options.TierPricingOptions()),
            Substitute.For<ILogger<WebhooksService>>());
    }

    // ── Double-Capture Prevention via PaymentIntent ────────────────────

    [Fact]
    public async Task DoubleCapture_FirstCapture_ShouldCreditLedger()
    {
        // Arrange: first capture wins TryCaptureAsync
        var intentId = Guid.NewGuid();
        var txId = Guid.NewGuid();
        var tx = BuildProcessingTransaction(txId, "pi_test_001", intentId);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_001").Returns(tx);
        _paymentIntentRepository.TryCaptureAsync(intentId, txId).Returns(true); // First capture wins

        SetupTenant();
        SetupRail();

        var payload = BuildPaymentIntentSucceeded("pi_test_001");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: ledger credited
        await _ledgerService.Received(1).RecordIncomingFundsAsync(
            TenantId, SellerId, 95m, Arg.Any<LedgerAccountType>(), Arg.Any<string>());

        // Assert: committed
        await _unitOfWork.Received(1).CommitAsync();
    }

    [Fact]
    public async Task DoubleCapture_SecondCapture_ShouldNotCreditLedger_AndEnqueueReconciliation()
    {
        // Arrange: second capture loses TryCaptureAsync
        var intentId = Guid.NewGuid();
        var txId = Guid.NewGuid();
        var tx = BuildProcessingTransaction(txId, "pi_test_002", intentId);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_002").Returns(tx);
        _paymentIntentRepository.TryCaptureAsync(intentId, txId).Returns(false); // Collision!

        SetupTenant();
        SetupRail();

        var payload = BuildPaymentIntentSucceeded("pi_test_002");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: NO ledger credit
        await _ledgerService.DidNotReceive().RecordIncomingFundsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<LedgerAccountType>(), Arg.Any<string>());

        // Assert: reconciliation enqueued for DOUBLE_CAPTURE tracking
        _backgroundJobs.Received(1).Enqueue<IReconciliationService>(
            Arg.Any<System.Linq.Expressions.Expression<Func<IReconciliationService, Task>>>());

        // Assert: transaction status was still set to CAPTURED (it was a legit Stripe event)
        await _transactionRepository.Received(1).SetStatusAsync(txId, TransactionStatus.CAPTURED);

        // Assert: committed (to persist status change)
        await _unitOfWork.Received(1).CommitAsync();
    }

    // ── Fallback: ExternalReferenceId-based Collision Guard ────────────

    [Fact]
    public async Task ExternalReferenceCollision_ShouldNotCreditLedger_WhenCapturedTxAlreadyExists()
    {
        // Arrange: transaction without PaymentIntent but with ExternalReferenceId
        var txId = Guid.NewGuid();
        var tx = BuildProcessingTransaction(txId, "pi_test_003", paymentIntentId: null,
            externalReferenceId: "order-456");

        var existingCapturedTx = BuildProcessingTransaction(Guid.NewGuid(), "pi_existing", null, "order-456");
        typeof(Transaction).GetProperty("Status")!.SetValue(existingCapturedTx, TransactionStatus.CAPTURED);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_003").Returns(tx);
        _transactionRepository.GetCapturedByExternalReferenceAsync(TenantId, "order-456", txId)
            .Returns(existingCapturedTx);

        SetupTenant();
        SetupRail();

        var payload = BuildPaymentIntentSucceeded("pi_test_003");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: NO ledger credit
        await _ledgerService.DidNotReceive().RecordIncomingFundsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<LedgerAccountType>(), Arg.Any<string>());

        // Assert: reconciliation enqueued
        _backgroundJobs.Received(1).Enqueue<IReconciliationService>(
            Arg.Any<System.Linq.Expressions.Expression<Func<IReconciliationService, Task>>>());
    }

    // ── Idempotent Webhook: Same Status → No-Op ────────────────────────

    [Fact]
    public async Task StripeWebhook_SameStatus_ShouldBeIdempotent()
    {
        // Arrange: transaction already CAPTURED, receiving another payment_intent.succeeded
        var txId = Guid.NewGuid();
        var tx = BuildProcessingTransaction(txId, "pi_test_004", null);
        tx.UpdateStatus(TransactionStatus.CAPTURED);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_004").Returns(tx);

        var payload = BuildPaymentIntentSucceeded("pi_test_004");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: no ledger operations
        await _ledgerService.DidNotReceive().RecordIncomingFundsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<LedgerAccountType>(), Arg.Any<string>());
        await _unitOfWork.DidNotReceive().BeginAsync();
    }

    // ── Invalid Transition → Ignored ────────────────────────────────

    [Fact]
    public async Task StripeWebhook_InvalidTransition_ShouldBeIgnored()
    {
        // Arrange: DECLINED transaction receiving succeeded (invalid transition)
        var txId = Guid.NewGuid();
        var tx = BuildProcessingTransaction(txId, "pi_test_005", null);
        tx.UpdateStatus(TransactionStatus.DECLINED);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_005").Returns(tx);

        var payload = BuildPaymentIntentSucceeded("pi_test_005");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: no state change
        await _transactionRepository.DidNotReceive().SetStatusAsync(
            Arg.Any<Guid>(), Arg.Any<TransactionStatus>());
        await _unitOfWork.DidNotReceive().BeginAsync();
    }

    // ── Direct Charge: Concurrent Capture Uses Correct Ledger Method ───

    [Fact]
    public async Task DirectCharge_FirstCapture_ShouldUseRecordDirectChargeFunds()
    {
        // Arrange
        var intentId = Guid.NewGuid();
        var txId = Guid.NewGuid();
        var tx = BuildProcessingTransaction(txId, "pi_direct_001", intentId);

        _transactionRepository.GetByProviderTxIdAsync("pi_direct_001").Returns(tx);
        _paymentIntentRepository.TryCaptureAsync(intentId, txId).Returns(true);

        SetupDirectChargeTenant();
        SetupRail();
        SetupSellerForDirectCharge();

        // DirectCharge webhooks include Account field matching seller's connected account
        var payload = BuildPaymentIntentSucceeded("pi_direct_001", account: "acct_test_123");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: RecordDirectChargeFundsAsync used (not RecordIncomingFundsAsync)
        await _ledgerService.Received(1).RecordDirectChargeFundsAsync(
            TenantId, SellerId, 95m, 5m, Arg.Any<LedgerAccountType>(), Arg.Any<string>());

        await _ledgerService.DidNotReceive().RecordIncomingFundsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<LedgerAccountType>(), Arg.Any<string>());
    }

    // ── PaymentIntent In-Memory TryCapture ─────────────────────────────

    [Fact]
    public void PaymentIntent_TryCapture_FirstCaller_ShouldWin()
    {
        var intent = PaymentIntent.Create(TenantId, "order-001", 100m, SellerId);
        var tx1 = Guid.NewGuid();
        var tx2 = Guid.NewGuid();

        bool first = intent.TryCapture(tx1);
        bool second = intent.TryCapture(tx2);

        first.Should().BeTrue();
        second.Should().BeFalse();
        intent.CapturedTransactionId.Should().Be(tx1);
        intent.Status.Should().Be(PaymentIntentStatus.CAPTURED);
    }

    [Fact]
    public void PaymentIntent_IsAlreadyCaptured_ShouldReflectState()
    {
        var intent = PaymentIntent.Create(TenantId, "order-002", 100m);

        intent.IsAlreadyCaptured.Should().BeFalse();
        intent.TryCapture(Guid.NewGuid());
        intent.IsAlreadyCaptured.Should().BeTrue();
    }

    // ── WebhooksService Rollback on Error ──────────────────────────────

    [Fact]
    public async Task StripeWebhook_WhenLedgerThrows_ShouldRollbackAndRethrow()
    {
        // Arrange
        var intentId = Guid.NewGuid();
        var txId = Guid.NewGuid();
        var tx = BuildProcessingTransaction(txId, "pi_fail_001", intentId);

        _transactionRepository.GetByProviderTxIdAsync("pi_fail_001").Returns(tx);
        _paymentIntentRepository.TryCaptureAsync(intentId, txId).Returns(true);

        SetupTenant();
        SetupRail();

        _ledgerService.RecordIncomingFundsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<LedgerAccountType>(), Arg.Any<string>())
            .Returns(Task.FromException(new Exception("DB connection failed")));

        var payload = BuildPaymentIntentSucceeded("pi_fail_001");

        // Act
        var act = () => _sut.HandleStripeEventAsync(payload);

        // Assert
        await act.Should().ThrowAsync<Exception>().WithMessage("DB connection failed");
        await _unitOfWork.Received(1).RollbackAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private Transaction BuildProcessingTransaction(
        Guid txId, string providerTxId, Guid? paymentIntentId,
        string? externalReferenceId = null)
    {
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.CREDIT_CARD, PaymentProvider.STRIPE, 1,
            feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: providerTxId,
            sellerId: SellerId,
            externalReferenceId: externalReferenceId);

        var tx = result.Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(tx, txId);

        if (paymentIntentId.HasValue)
            tx.SetPaymentIntentId(paymentIntentId.Value);

        return tx;
    }

    private void SetupTenant()
    {
        var tenant = Tenant.Create("TestTenant", "test", "hash", "fp_", "secret");
        typeof(Tenant).GetProperty("Id")!.SetValue(tenant, TenantId);
        var config = TenantConfig.Create(TenantId);
        typeof(Tenant).GetProperty("Config")!.SetValue(tenant, config);
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);
    }

    private void SetupDirectChargeTenant()
    {
        var tenant = Tenant.Create("TestTenant", "test", "hash", "fp_", "secret");
        typeof(Tenant).GetProperty("Id")!.SetValue(tenant, TenantId);
        var config = TenantConfig.Create(TenantId);
        config.SetStripeChargeMode(StripeChargeMode.DIRECT_CHARGE);
        typeof(Tenant).GetProperty("Config")!.SetValue(tenant, config);
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);
    }

    private void SetupRail()
    {
        var rail = Substitute.For<IPaymentRail>();
        rail.CaptureAccountType.Returns(LedgerAccountType.FUTURE_RECEIVABLES);
        rail.LedgerPolicy.Returns(new LedgerPolicy(
            LedgerAccountType.FUTURE_RECEIVABLES,
            SupportsDispute: true,
            SupportsRefund: true,
            HasSettlementDelay: true));
        _railRouter.ResolveRailForTransaction(Arg.Any<Transaction>()).Returns(rail);
    }

    private void SetupSellerForDirectCharge()
    {
        var seller = Seller.Create(
            TenantId, "Seller Ltda", "12345678000100", "seller@test.com",
            "secret-123", PaymentProvider.STRIPE, externalAccountId: "acct_test_123");
        typeof(Seller).GetProperty("Id")!.SetValue(seller, SellerId);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
    }

    private static StripeWebhookDto BuildPaymentIntentSucceeded(string piId, string? account = null)
    {
        return new StripeWebhookDto(
            Id: $"evt_{Guid.NewGuid():N}",
            Type: "payment_intent.succeeded",
            Data: new StripeWebhookData(
                Object: new StripeWebhookObject(Id: piId)),
            Account: account);
    }
}
