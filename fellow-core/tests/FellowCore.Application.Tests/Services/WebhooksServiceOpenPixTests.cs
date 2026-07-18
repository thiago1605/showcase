using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
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

namespace FellowCore.Application.Tests.Services;

public class WebhooksServiceOpenPixTests
{
    private readonly ITransactionRepository _transactionRepo = Substitute.For<ITransactionRepository>();
    private readonly ISellerRepository _sellerRepo = Substitute.For<ISellerRepository>();
    private readonly IWebhookEndpointRepository _webhookEndpointRepo = Substitute.For<IWebhookEndpointRepository>();
    private readonly IWebhookDeliveryRepository _webhookDeliveryRepo = Substitute.For<IWebhookDeliveryRepository>();
    private readonly IInboundWebhookEventRepository _inboundRepo = Substitute.For<IInboundWebhookEventRepository>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly ISecurityService _securityService = Substitute.For<ISecurityService>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly ILogger<WebhooksService> _logger = Substitute.For<ILogger<WebhooksService>>();
    private readonly WebhooksService _sut;

    private const string TestPlatformAppId = "test-platform-appid-001";

    public WebhooksServiceOpenPixTests()
    {
        _configuration["OpenPix:AppId"].Returns(TestPlatformAppId);
        // Default: inbound dedup guard "deixa passar" — todos os testes vêem o flow real.
        // Pra testar dedup, override o setup via _inboundRepo.TryRegisterReceivedAsync(...).Returns((InboundWebhookEvent?)null);
        _inboundRepo.TryRegisterReceivedAsync(
                Arg.Any<PaymentProvider>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => InboundWebhookEvent.CreateReceived(
                (PaymentProvider)call[0], (string)call[1], (string)call[2]));

        var providerFactory = Substitute.For<IPaymentProviderFactory>();
        var railRouter = new RailRouter([new StripeCardRail(providerFactory), new StripeBoletoRail(providerFactory), new OpenPixRail(providerFactory)]);
        _sut = new WebhooksService(_transactionRepo, Substitute.For<ITransactionInstallmentRepository>(), _sellerRepo, _tenantRepo, _webhookEndpointRepo, _webhookDeliveryRepo, _inboundRepo, _ledgerService, _securityService, _configuration, _unitOfWork, Substitute.For<IBackgroundJobs>(), railRouter, Substitute.For<IPaymentIntentRepository>(), Substitute.For<IDisputeRepository>(), Substitute.For<ISplitTransferRepository>(), Substitute.For<IDomainEventDispatcher>(), Substitute.For<IWebhookProbeClient>(), Substitute.For<IAppMetrics>(), Substitute.For<FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator>(), Microsoft.Extensions.Options.Options.Create(new FellowCore.Application.Modules.Pricing.Options.TierPricingOptions()), _logger);
    }

    [Fact]
    public async Task HandleOpenPixEventAsync_ShouldUpdateToCaptured_OnChargeCompleted()
    {
        var transaction = BuildTransaction(TransactionStatus.PROCESSING);
        _transactionRepo.GetByProviderTxIdAsync("corr-001").Returns(transaction);

        var payload = new OpenPixWebhookDto(
            Event: "OPENPIX:CHARGE_COMPLETED",
            Charge: new OpenPixWebhookCharge("COMPLETED", "corr-001", "tx-001", null, null, null),
            Pix: null);

        await _sut.HandleOpenPixEventAsync(payload, TestPlatformAppId);

        await _transactionRepo.Received(1).SetStatusAsync(transaction.Id, TransactionStatus.CAPTURED);
        await _unitOfWork.Received(1).CommitAsync();
    }

    [Fact]
    public async Task HandleOpenPixEventAsync_ShouldUpdateToDeclined_OnChargeExpired()
    {
        var transaction = BuildTransaction(TransactionStatus.PROCESSING);
        _transactionRepo.GetByProviderTxIdAsync("corr-002").Returns(transaction);

        var payload = new OpenPixWebhookDto(
            Event: "OPENPIX:CHARGE_EXPIRED",
            Charge: new OpenPixWebhookCharge("EXPIRED", "corr-002", "tx-002", null, null, null),
            Pix: null);

        await _sut.HandleOpenPixEventAsync(payload, TestPlatformAppId);

        await _transactionRepo.Received(1).SetStatusAsync(transaction.Id, TransactionStatus.DECLINED);
    }

    [Fact]
    public async Task HandleOpenPixEventAsync_ShouldUpdateToRefunded_OnRefund()
    {
        var transaction = BuildTransaction(TransactionStatus.CAPTURED);
        _transactionRepo.GetByProviderTxIdAsync("corr-003").Returns(transaction);

        var payload = new OpenPixWebhookDto(
            Event: "OPENPIX:TRANSACTION_REFUND_RECEIVED",
            Charge: new OpenPixWebhookCharge("REFUNDED", "corr-003", "tx-003", null, null, null),
            Pix: null);

        await _sut.HandleOpenPixEventAsync(payload, TestPlatformAppId);

        await _transactionRepo.Received(1).SetStatusAsync(transaction.Id, TransactionStatus.REFUNDED);
    }

    [Fact]
    public async Task HandleOpenPixEventAsync_ShouldIgnore_WhenTransactionNotFound()
    {
        _transactionRepo.GetByProviderTxIdAsync("unknown").Returns((Transaction?)null);

        var payload = new OpenPixWebhookDto(
            Event: "OPENPIX:CHARGE_COMPLETED",
            Charge: new OpenPixWebhookCharge("COMPLETED", "unknown", "tx-x", null, null, null),
            Pix: null);

        await _sut.HandleOpenPixEventAsync(payload);

        await _unitOfWork.DidNotReceive().CommitAsync();
    }

    [Fact]
    public async Task HandleOpenPixEventAsync_ShouldIgnore_WhenStatusAlreadyMatches()
    {
        var transaction = BuildTransaction(TransactionStatus.CAPTURED);
        _transactionRepo.GetByProviderTxIdAsync("corr-dup").Returns(transaction);

        var payload = new OpenPixWebhookDto(
            Event: "OPENPIX:CHARGE_COMPLETED",
            Charge: new OpenPixWebhookCharge("COMPLETED", "corr-dup", "tx-dup", null, null, null),
            Pix: null);

        await _sut.HandleOpenPixEventAsync(payload);

        await _unitOfWork.DidNotReceive().BeginAsync();
    }

    [Fact]
    public async Task HandleOpenPixEventAsync_ShouldIgnore_UnknownEvent()
    {
        var payload = new OpenPixWebhookDto(
            Event: "OPENPIX:SOME_UNKNOWN_EVENT",
            Charge: new OpenPixWebhookCharge("UNKNOWN", "corr-unknown", "tx-u", null, null, null),
            Pix: null);

        await _sut.HandleOpenPixEventAsync(payload);

        await _unitOfWork.DidNotReceive().BeginAsync();
    }

    [Fact]
    public async Task HandleOpenPixEventAsync_ShouldActivateSeller_OnAccountRegisterApproved()
    {
        var seller = BuildSeller();
        _sellerRepo.GetByExternalAccountIdAsync("reg-001").Returns(seller);

        var payload = new OpenPixWebhookDto(
            Event: "ACCOUNT_REGISTER_APPROVED",
            Charge: null,
            Pix: null,
            AccountRegister: new OpenPixWebhookAccountRegister("reg-001", "APPROVED"));

        await _sut.HandleOpenPixEventAsync(payload, TestPlatformAppId);

        seller.Status.Should().Be(SellerStatus.ACTIVE);
        await _sellerRepo.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task HandleOpenPixEventAsync_ShouldSuspendSeller_OnAccountRegisterRejected()
    {
        var seller = BuildSeller();
        _sellerRepo.GetByExternalAccountIdAsync("reg-002").Returns(seller);

        var payload = new OpenPixWebhookDto(
            Event: "ACCOUNT_REGISTER_REJECTED",
            Charge: null,
            Pix: null,
            AccountRegister: new OpenPixWebhookAccountRegister("reg-002", "REJECTED"));

        await _sut.HandleOpenPixEventAsync(payload, TestPlatformAppId);

        seller.Status.Should().Be(SellerStatus.SUSPENDED);
    }

    [Fact]
    public async Task HandleOpenPixEventAsync_ShouldReject_AccountRegister_WithoutToken()
    {
        var payload = new OpenPixWebhookDto(
            Event: "ACCOUNT_REGISTER_APPROVED",
            Charge: null,
            Pix: null,
            AccountRegister: new OpenPixWebhookAccountRegister("reg-003", "APPROVED"));

        await _sut.HandleOpenPixEventAsync(payload); // no token

        await _sellerRepo.DidNotReceive().GetByExternalAccountIdAsync(Arg.Any<string>());
    }

    private static Transaction BuildTransaction(TransactionStatus status)
    {
        var result = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: 100m,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.OPENPIX,
            installments: 1,
            feeAmount: 2m,
            netAmount: 98m,
            expectedSettlementDate: null,
            providerTxId: "will-be-overridden",
            sellerId: Guid.NewGuid());

        var transaction = result.Value;

        if (status != TransactionStatus.CREATED && status != TransactionStatus.PROCESSING)
            transaction.UpdateStatus(status);

        return transaction;
    }

    private static Seller BuildSeller() =>
        Seller.Create(
            tenantId: Guid.NewGuid(),
            legalName: "Seller Test",
            document: "12345678901",
            email: "seller@test.com",
            webhookSecret: "secret-32-chars-long-enough!!!!!!",
            preferredProvider: PaymentProvider.OPENPIX,
            encryptedAccessToken: "token",
            feeDebit: 0m,
            feePixIn: 0m);

    // ── Stripe Webhook Connect Validation ────────────────────────────────

    [Fact]
    public async Task HandleStripeEvent_ShouldReject_WhenAccountMismatchesSeller()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 100m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 1, feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: null, providerTxId: "pi_mismatch_001", sellerId: sellerId);
        var transaction = txResult.Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(transaction, Guid.NewGuid());

        _transactionRepo.GetByProviderTxIdAsync("pi_mismatch_001").Returns(transaction);

        // Seller has a different ExternalAccountId than the webhook's account field
        var seller = Seller.Create(tenantId, "Seller", "12345678901", "s@test.com",
            "secret-32-chars-long-enough!!!!!", PaymentProvider.STRIPE,
            externalAccountId: "acct_correct_123");
        typeof(Seller).GetProperty("Id")!.SetValue(seller, sellerId);
        _sellerRepo.GetByIdAsync(tenantId, sellerId).Returns(seller);

        var payload = new StripeWebhookDto(
            Id: "evt_test", Type: "payment_intent.succeeded",
            Data: new StripeWebhookData(new StripeWebhookObject("pi_mismatch_001")),
            Account: "acct_wrong_999");

        await _sut.HandleStripeEventAsync(payload);

        // Should not commit because account doesn't match
        await _unitOfWork.DidNotReceive().BeginAsync();
    }

    [Fact]
    public async Task HandleStripeEvent_ShouldProcess_WhenAccountMatchesSeller()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 100m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 1, feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: null, providerTxId: "pi_match_001", sellerId: sellerId);
        var transaction = txResult.Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(transaction, Guid.NewGuid());

        _transactionRepo.GetByProviderTxIdAsync("pi_match_001").Returns(transaction);

        var seller = Seller.Create(tenantId, "Seller", "12345678901", "s@test.com",
            "secret-32-chars-long-enough!!!!!", PaymentProvider.STRIPE,
            externalAccountId: "acct_correct_123");
        typeof(Seller).GetProperty("Id")!.SetValue(seller, sellerId);
        _sellerRepo.GetByIdAsync(tenantId, sellerId).Returns(seller);

        var payload = new StripeWebhookDto(
            Id: "evt_test", Type: "payment_intent.succeeded",
            Data: new StripeWebhookData(new StripeWebhookObject("pi_match_001")),
            Account: "acct_correct_123");

        await _sut.HandleStripeEventAsync(payload);

        // Should proceed to process — unitOfWork.BeginAsync is called
        await _unitOfWork.Received(1).BeginAsync();
    }

    [Fact]
    public async Task HandleStripeEvent_DirectCharge_NoAccountField_ShouldReject()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 100m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 1, feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: null, providerTxId: "pi_dc_no_acct", sellerId: sellerId);
        var transaction = txResult.Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(transaction, Guid.NewGuid());

        _transactionRepo.GetByProviderTxIdAsync("pi_dc_no_acct").Returns(transaction);

        // Tenant configured for DIRECT_CHARGE
        var tenant = Tenant.Create("DC Tenant", "dc-slug", "hash", "fp_", "secret_hash");
        typeof(Tenant).GetProperty("Id")!.SetValue(tenant, tenantId);
        var config = tenant.CreateDefaultConfig();
        config.SetStripeChargeMode(StripeChargeMode.DIRECT_CHARGE);
        _tenantRepo.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        // Webhook without account field (should be rejected for DirectCharge)
        var payload = new StripeWebhookDto(
            Id: "evt_no_acct", Type: "payment_intent.succeeded",
            Data: new StripeWebhookData(new StripeWebhookObject("pi_dc_no_acct")),
            Account: null);

        await _sut.HandleStripeEventAsync(payload);

        // Should NOT process — unitOfWork.BeginAsync should not be called
        await _unitOfWork.DidNotReceive().BeginAsync();
    }

    [Fact]
    public async Task HandleStripeEvent_DestinationCharge_NoAccountField_ShouldAccept()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 100m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 1, feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: null, providerTxId: "pi_dest_no_acct", sellerId: sellerId);
        var transaction = txResult.Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(transaction, Guid.NewGuid());

        _transactionRepo.GetByProviderTxIdAsync("pi_dest_no_acct").Returns(transaction);

        // Tenant configured for DESTINATION_CHARGE (default)
        var tenant = Tenant.Create("Dest Tenant", "dest-slug", "hash", "fp_", "secret_hash");
        typeof(Tenant).GetProperty("Id")!.SetValue(tenant, tenantId);
        tenant.CreateDefaultConfig(); // default = DESTINATION_CHARGE
        _tenantRepo.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        var payload = new StripeWebhookDto(
            Id: "evt_dest", Type: "payment_intent.succeeded",
            Data: new StripeWebhookData(new StripeWebhookObject("pi_dest_no_acct")),
            Account: null);

        await _sut.HandleStripeEventAsync(payload);

        // Should proceed — platform/destination events without account are valid
        await _unitOfWork.Received(1).BeginAsync();
    }
}
