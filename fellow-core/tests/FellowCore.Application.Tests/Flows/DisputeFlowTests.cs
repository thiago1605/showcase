using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Tests.Helpers;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
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
/// T13: Dispute flow tests. Validates dispute created -> ledger hold,
/// dispute won -> release hold + restore seller, dispute lost -> settle loss + reverse fee.
/// These are FLOW tests exercising WebhooksService -> LedgerService interaction.
/// </summary>
public class DisputeFlowTests
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
    private static readonly Guid TransactionId = Guid.NewGuid();
    private static readonly Guid PaymentIntentId = Guid.NewGuid();

    public DisputeFlowTests()
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

    // ── Dispute Created → Ledger Hold ──────────────────────────────────

    [Fact]
    public async Task DisputeCreated_ShouldHoldFundsInLedger_AndSetChargebackStatus()
    {
        // Arrange: captured transaction with seller
        var transaction = BuildCapturedTransaction();
        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns((Dispute?)null);

        SetupDestinationChargeTenant();

        var payload = BuildDisputePayload("charge.dispute.created", "dp_test_001", "pi_test_123", 10000, "needs_response");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: transaction set to CHARGEBACKERROR
        await _transactionRepository.Received(1).SetStatusAsync(TransactionId, TransactionStatus.CHARGEBACKERROR);

        // Assert: dispute entity created
        _disputeRepository.Received(1).Add(Arg.Is<Dispute>(d =>
            d.TransactionId == TransactionId &&
            d.ExternalDisputeId == "dp_test_001" &&
            d.TenantId == TenantId));
        await _disputeRepository.Received(1).SaveChangesAsync();

        // Assert: ledger hold executed with the transaction's NetAmount (95m)
        await _ledgerService.Received(1).HoldDisputeAsync(
            TenantId, SellerId, 95m,
            Arg.Is<string>(s => s.Contains("dp_test_001")),
            TransactionId.ToString());
    }

    [Fact]
    public async Task DisputeCreated_WhenDisputeAlreadyExists_ShouldBeIdempotent()
    {
        // Arrange
        var transaction = BuildCapturedTransaction();
        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);

        var existingDispute = Dispute.Create(TenantId, TransactionId, SellerId, "dp_test_001", 95m);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns(existingDispute);

        SetupDestinationChargeTenant();

        var payload = BuildDisputePayload("charge.dispute.created", "dp_test_001", "pi_test_123", 10000, "needs_response");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: no new dispute added, no ledger hold
        _disputeRepository.DidNotReceive().Add(Arg.Any<Dispute>());
        await _ledgerService.DidNotReceive().HoldDisputeAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DisputeCreated_WhenAlreadyChargebackError_ShouldBeIdempotent()
    {
        // Arrange: transaction already in CHARGEBACKERROR
        var transaction = BuildCapturedTransaction();
        // Use reflection to simulate already-chargeback status
        typeof(Transaction).GetProperty("Status")!.SetValue(transaction, TransactionStatus.CHARGEBACKERROR);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns((Dispute?)null);

        SetupDestinationChargeTenant();

        var payload = BuildDisputePayload("charge.dispute.created", "dp_test_001", "pi_test_123", 10000, "needs_response");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: no new dispute, no ledger hold
        _disputeRepository.DidNotReceive().Add(Arg.Any<Dispute>());
        await _ledgerService.DidNotReceive().HoldDisputeAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DisputeCreated_ShouldMarkPaymentIntentAsDisputed_WhenLinked()
    {
        // Arrange
        var transaction = BuildCapturedTransaction();
        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns((Dispute?)null);

        var intent = PaymentIntent.Create(TenantId, "order-123", 100m, SellerId);
        intent.TryCapture(TransactionId);
        _paymentIntentRepository.GetByIdAsync(TenantId, PaymentIntentId).Returns(intent);

        SetupDestinationChargeTenant();

        var payload = BuildDisputePayload("charge.dispute.created", "dp_test_001", "pi_test_123", 10000, "needs_response");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: PaymentIntent status should be DISPUTED
        intent.Status.Should().Be(PaymentIntentStatus.DISPUTED);
    }

    // ── Dispute Won → Release Hold + Restore Seller ────────────────────

    [Fact]
    public async Task DisputeWon_ShouldReleaseHoldAndRestoreSellerBalance()
    {
        // Arrange
        var transaction = BuildCapturedTransaction();
        // Set status to CHARGEBACKERROR (as it would be after dispute.created)
        typeof(Transaction).GetProperty("Status")!.SetValue(transaction, TransactionStatus.CHARGEBACKERROR);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);

        var dispute = Dispute.Create(TenantId, TransactionId, SellerId, "dp_test_001", 95m);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns(dispute);

        SetupDestinationChargeTenant();

        var payload = BuildDisputePayload("charge.dispute.closed", "dp_test_001", "pi_test_123", 10000, "won");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: ledger released
        await _ledgerService.Received(1).ReleaseDisputeAsync(
            TenantId, SellerId, 95m,
            Arg.Is<string>(s => s.Contains("dp_test_001")),
            TransactionId.ToString());

        // Assert: transaction restored to CAPTURED
        await _transactionRepository.Received(1).SetStatusAsync(TransactionId, TransactionStatus.CAPTURED);

        // Assert: dispute entity updated to WON
        dispute.Status.Should().Be(DisputeStatus.WON);
        _disputeRepository.Received(1).Update(dispute);
        await _disputeRepository.Received(1).SaveChangesAsync();
    }

    // ── Dispute Lost → Settle Loss + Reverse Platform Fee (Direct Charge) ──

    [Fact]
    public async Task DisputeLost_ShouldSettleLossInLedger()
    {
        // Arrange
        var transaction = BuildCapturedTransaction();
        typeof(Transaction).GetProperty("Status")!.SetValue(transaction, TransactionStatus.CHARGEBACKERROR);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);

        var dispute = Dispute.Create(TenantId, TransactionId, SellerId, "dp_test_001", 95m);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns(dispute);

        SetupDestinationChargeTenant();

        var payload = BuildDisputePayload("charge.dispute.closed", "dp_test_001", "pi_test_123", 10000, "lost");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: settle dispute loss in ledger (debit DISPUTE, credit PLATFORM_PAYOUT)
        await _ledgerService.Received(1).SettleDisputeLossAsync(
            TenantId, SellerId, 95m,
            Arg.Is<string>(s => s.Contains("dp_test_001")),
            TransactionId.ToString());

        // Assert: dispute entity updated to LOST
        dispute.Status.Should().Be(DisputeStatus.LOST);
        _disputeRepository.Received(1).Update(dispute);
        await _disputeRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task DisputeLost_DirectCharge_ShouldSettleDisputeFeeLoss()
    {
        // Arrange: Direct Charge tenant with fee
        var transaction = BuildCapturedTransaction();
        typeof(Transaction).GetProperty("Status")!.SetValue(transaction, TransactionStatus.CHARGEBACKERROR);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);

        var dispute = Dispute.Create(TenantId, TransactionId, SellerId, "dp_test_001", 95m);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns(dispute);

        // Setup Direct Charge tenant + seller with matching connected account
        SetupDirectChargeTenant();
        SetupSellerForDirectCharge();

        // DirectCharge webhooks include Account field matching seller's connected account
        var payload = BuildDisputePayload("charge.dispute.closed", "dp_test_001", "pi_test_123", 10000, "lost", account: "acct_test_123");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: L11 — dispute fee loss settled from DISPUTE_FEE (5m = 100m gross - 95m net)
        await _ledgerService.Received(1).SettleDisputeFeeLossAsync(
            TenantId, 5m,
            Arg.Is<string>(s => s.Contains("dp_test_001")),
            TransactionId.ToString());
    }

    [Fact]
    public async Task DisputeLost_DestinationCharge_ShouldNotSettleDisputeFeeLoss()
    {
        // Arrange: Destination Charge tenant (default)
        var transaction = BuildCapturedTransaction();
        typeof(Transaction).GetProperty("Status")!.SetValue(transaction, TransactionStatus.CHARGEBACKERROR);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);

        var dispute = Dispute.Create(TenantId, TransactionId, SellerId, "dp_test_001", 95m);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns(dispute);

        SetupDestinationChargeTenant();

        var payload = BuildDisputePayload("charge.dispute.closed", "dp_test_001", "pi_test_123", 10000, "lost");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: no dispute fee loss for destination charge
        await _ledgerService.DidNotReceive().SettleDisputeFeeLossAsync(
            Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ── L11: Direct Charge Fee Hold/Release During Dispute ────────────

    [Fact]
    public async Task DisputeCreated_DirectCharge_ShouldHoldPlatformFee()
    {
        // Arrange
        var transaction = BuildCapturedTransaction();
        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns((Dispute?)null);

        SetupDirectChargeTenant();
        SetupSellerForDirectCharge();

        var payload = BuildDisputePayload("charge.dispute.created", "dp_test_001", "pi_test_123", 10000, "needs_response", account: "acct_test_123");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: L11 — platform fee frozen (5m = 100m gross - 95m net)
        await _ledgerService.Received(1).HoldDisputeFeeAsync(
            TenantId, 5m,
            Arg.Is<string>(s => s.Contains("dp_test_001")),
            TransactionId.ToString());
    }

    [Fact]
    public async Task DisputeCreated_DestinationCharge_ShouldNotHoldPlatformFee()
    {
        // Arrange
        var transaction = BuildCapturedTransaction();
        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns((Dispute?)null);

        SetupDestinationChargeTenant();

        var payload = BuildDisputePayload("charge.dispute.created", "dp_test_001", "pi_test_123", 10000, "needs_response");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: no fee hold for destination charge
        await _ledgerService.DidNotReceive().HoldDisputeFeeAsync(
            Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DisputeWon_DirectCharge_ShouldReleasePlatformFeeHold()
    {
        // Arrange
        var transaction = BuildCapturedTransaction();
        typeof(Transaction).GetProperty("Status")!.SetValue(transaction, TransactionStatus.CHARGEBACKERROR);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);

        var dispute = Dispute.Create(TenantId, TransactionId, SellerId, "dp_test_001", 95m);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns(dispute);

        SetupDirectChargeTenant();
        SetupSellerForDirectCharge();

        var payload = BuildDisputePayload("charge.dispute.closed", "dp_test_001", "pi_test_123", 10000, "won", account: "acct_test_123");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: L11 — platform fee released (5m = 100m gross - 95m net)
        await _ledgerService.Received(1).ReleaseDisputeFeeAsync(
            TenantId, 5m,
            Arg.Is<string>(s => s.Contains("dp_test_001")),
            TransactionId.ToString());
    }

    [Fact]
    public async Task DisputeWon_DestinationCharge_ShouldNotReleasePlatformFeeHold()
    {
        // Arrange
        var transaction = BuildCapturedTransaction();
        typeof(Transaction).GetProperty("Status")!.SetValue(transaction, TransactionStatus.CHARGEBACKERROR);

        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);

        var dispute = Dispute.Create(TenantId, TransactionId, SellerId, "dp_test_001", 95m);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns(dispute);

        SetupDestinationChargeTenant();

        var payload = BuildDisputePayload("charge.dispute.closed", "dp_test_001", "pi_test_123", 10000, "won");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: no fee release for destination charge
        await _ledgerService.DidNotReceive().ReleaseDisputeFeeAsync(
            Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DisputeCreated_WhenLedgerHoldFails_ShouldStillPersistDispute()
    {
        // Arrange
        var transaction = BuildCapturedTransaction();
        _transactionRepository.GetByProviderTxIdAsync("pi_test_123").Returns(transaction);
        _disputeRepository.GetByExternalIdAsync("dp_test_001").Returns((Dispute?)null);

        SetupDestinationChargeTenant();

        _ledgerService.HoldDisputeAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>())
            .Throws(new Exception("Insufficient balance"));

        var payload = BuildDisputePayload("charge.dispute.created", "dp_test_001", "pi_test_123", 10000, "needs_response");

        // Act
        await _sut.HandleStripeEventAsync(payload);

        // Assert: dispute entity was still persisted despite ledger failure
        _disputeRepository.Received(1).Add(Arg.Any<Dispute>());
        await _disputeRepository.Received(1).SaveChangesAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private Transaction BuildCapturedTransaction()
    {
        var result = Transaction.Create(
            TenantId, 100m, PaymentType.CREDIT_CARD, PaymentProvider.STRIPE, 1,
            feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_test_123",
            sellerId: SellerId);

        var tx = result.Value;

        // Set Id via reflection (protected set)
        typeof(Transaction).GetProperty("Id")!.SetValue(tx, TransactionId);

        // Set PaymentIntentId
        tx.SetPaymentIntentId(PaymentIntentId);

        // Transition to CAPTURED
        tx.UpdateStatus(TransactionStatus.CAPTURED);

        return tx;
    }

    private void SetupDestinationChargeTenant()
    {
        var tenant = Tenant.Create("TestTenant", "test", "hash", "fp_", "secret");
        typeof(Tenant).GetProperty("Id")!.SetValue(tenant, TenantId);

        var config = TenantConfig.Create(TenantId);
        // Default is DESTINATION_CHARGE

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

    private void SetupSellerForDirectCharge()
    {
        var seller = Seller.Create(
            TenantId, "Seller Ltda", "12345678000100", "seller@test.com",
            "secret-123", PaymentProvider.STRIPE, externalAccountId: "acct_test_123");
        typeof(Seller).GetProperty("Id")!.SetValue(seller, SellerId);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
    }

    private static StripeWebhookDto BuildDisputePayload(
        string eventType, string disputeId, string paymentIntentId,
        long amountCents, string status, string? account = null)
    {
        return new StripeWebhookDto(
            Id: $"evt_{Guid.NewGuid():N}",
            Type: eventType,
            Data: new StripeWebhookData(
                Object: new StripeWebhookObject(
                    Id: disputeId,
                    Status: status,
                    Amount: amountCents,
                    PaymentIntent: paymentIntentId)),
            Account: account);
    }
}
