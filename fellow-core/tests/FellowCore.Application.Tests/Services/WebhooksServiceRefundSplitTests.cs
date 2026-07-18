using System.Reflection;
using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Common;
using FellowCore.Application.Tests.Helpers;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Rails;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using FellowCore.Application.Modules.Webhooks.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class WebhooksServiceRefundSplitTests
{
    private readonly ITransactionRepository _transactionRepo = Substitute.For<ITransactionRepository>();
    private readonly ISellerRepository _sellerRepo = Substitute.For<ISellerRepository>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IWebhookEndpointRepository _webhookEndpointRepo = Substitute.For<IWebhookEndpointRepository>();
    private readonly IWebhookDeliveryRepository _webhookDeliveryRepo = Substitute.For<IWebhookDeliveryRepository>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly ISecurityService _securityService = Substitute.For<ISecurityService>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IBackgroundJobs _backgroundJobs = Substitute.For<IBackgroundJobs>();
    private readonly IPaymentIntentRepository _paymentIntentRepo = Substitute.For<IPaymentIntentRepository>();
    private readonly IDisputeRepository _disputeRepo = Substitute.For<IDisputeRepository>();
    private readonly ISplitTransferRepository _splitTransferRepo = Substitute.For<ISplitTransferRepository>();
    private readonly IDomainEventDispatcher _domainEventDispatcher = Substitute.For<IDomainEventDispatcher>();
    private readonly ILogger<WebhooksService> _logger = Substitute.For<ILogger<WebhooksService>>();
    private readonly WebhooksService _sut;

    public WebhooksServiceRefundSplitTests()
    {
        var providerFactory = Substitute.For<IPaymentProviderFactory>();
        var railRouter = new RailRouter([
            new StripeCardRail(providerFactory),
            new StripeBoletoRail(providerFactory),
            new OpenPixRail(providerFactory)
        ]);

        _sut = new WebhooksService(
            _transactionRepo,
            Substitute.For<ITransactionInstallmentRepository>(),
            _sellerRepo,
            _tenantRepo,
            _webhookEndpointRepo,
            _webhookDeliveryRepo,
            InboundWebhookGuardMockHelper.CreatePermissive(),
            _ledgerService,
            _securityService,
            _configuration,
            _unitOfWork,
            _backgroundJobs,
            railRouter,
            _paymentIntentRepo,
            _disputeRepo,
            _splitTransferRepo,
            _domainEventDispatcher,
            Substitute.For<IWebhookProbeClient>(),
            Substitute.For<IAppMetrics>(),
            Substitute.For<FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator>(),
            Microsoft.Extensions.Options.Options.Create(new FellowCore.Application.Modules.Pricing.Options.TierPricingOptions()),
            _logger);
    }

    [Fact]
    public async Task PartialRefund_Twice_ShouldDebitOnlyRemainingAmounts()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();

        var transaction = BuildTransaction(tenantId, amount: 100m, netAmount: 100m);

        var transferA = BuildPaidSplitTransfer(transaction.Id, tenantId, sellerA, amount: 50m);
        var transferB = BuildPaidSplitTransfer(transaction.Id, tenantId, sellerB, amount: 50m);

        var transfers = new List<SplitTransfer> { transferA, transferB };
        _splitTransferRepo.GetByTransactionIdAsync(tenantId, transaction.Id)
            .Returns(transfers);

        // Act — First partial refund of 30
        await InvokeReverseSplitsProportionallyAsync(transaction, 30m);

        // Assert — First refund: proportional split (30 * 50/100 = 15 each)
        await _ledgerService.Received(1).ReturnToClearingAsync(
            tenantId, sellerA, 15m, Arg.Any<string>(), Arg.Any<string>());
        await _ledgerService.Received(1).ReturnToClearingAsync(
            tenantId, sellerB, 15m, Arg.Any<string>(), Arg.Any<string>());
        await _ledgerService.Received(1).DrainClearingForRefundAsync(
            tenantId, 30m, Arg.Any<string>(), Arg.Any<string>());

        transferA.Status.Should().Be(SplitTransferStatus.PARTIALLY_REVERSED);
        transferA.RemainingAmount.Should().Be(35m);
        transferB.Status.Should().Be(SplitTransferStatus.PARTIALLY_REVERSED);
        transferB.RemainingAmount.Should().Be(35m);

        // Reset call counts for second invocation
        _ledgerService.ClearReceivedCalls();

        // Act — Second partial refund of 30, now RemainingAmount is 35 each (total reversible = 70)
        // proportionalRefund = 30 * (100/100) = 30, each gets Round(30 * 35 / 70) = 15
        await InvokeReverseSplitsProportionallyAsync(transaction, 30m);

        // Assert — Second refund uses reduced remaining amounts
        await _ledgerService.Received(1).ReturnToClearingAsync(
            tenantId, sellerA, 15m, Arg.Any<string>(), Arg.Any<string>());
        await _ledgerService.Received(1).ReturnToClearingAsync(
            tenantId, sellerB, 15m, Arg.Any<string>(), Arg.Any<string>());
        await _ledgerService.Received(1).DrainClearingForRefundAsync(
            tenantId, 30m, Arg.Any<string>(), Arg.Any<string>());

        transferA.RemainingAmount.Should().Be(20m);
        transferB.RemainingAmount.Should().Be(20m);
    }

    [Fact]
    public async Task FullRefund_AfterPartialRefund_ShouldReverseOnlyRemainingAmounts()
    {
        // Arrange: transfers already partially reversed (simulating prior partial refund)
        var tenantId = Guid.NewGuid();
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();

        var transaction = BuildTransaction(tenantId, amount: 100m, netAmount: 100m);

        // Each transfer was 50, already reversed 15, so RemainingAmount = 35
        var transferA = BuildPartiallyReversedSplitTransfer(transaction.Id, tenantId, sellerA, amount: 50m, reversedAmount: 15m);
        var transferB = BuildPartiallyReversedSplitTransfer(transaction.Id, tenantId, sellerB, amount: 50m, reversedAmount: 15m);

        var transfers = new List<SplitTransfer> { transferA, transferB };
        _splitTransferRepo.GetByTransactionIdAsync(tenantId, transaction.Id)
            .Returns(transfers);

        // Act — Full refund (refundDelta >= transaction.Amount triggers isFullRefund)
        await InvokeReverseSplitsProportionallyAsync(transaction, 100m);

        // Assert — Each transfer reversed by its full RemainingAmount (35 each)
        await _ledgerService.Received(1).ReturnToClearingAsync(
            tenantId, sellerA, 35m, Arg.Any<string>(), Arg.Any<string>());
        await _ledgerService.Received(1).ReturnToClearingAsync(
            tenantId, sellerB, 35m, Arg.Any<string>(), Arg.Any<string>());
        await _ledgerService.Received(1).DrainClearingForRefundAsync(
            tenantId, 70m, Arg.Any<string>(), Arg.Any<string>());

        transferA.Status.Should().Be(SplitTransferStatus.REVERSED);
        transferA.RemainingAmount.Should().Be(0m);
        transferB.Status.Should().Be(SplitTransferStatus.REVERSED);
        transferB.RemainingAmount.Should().Be(0m);
    }

    [Fact]
    public async Task Refund_WithPrimaryResidualTransfer_ShouldReversePrimaryThroughSplitTransferFlow()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var recipientSeller = Guid.NewGuid();
        var primarySeller = Guid.NewGuid();

        var transaction = BuildTransaction(tenantId, amount: 100m, netAmount: 100m);

        var recipientTransfer = BuildPaidSplitTransfer(transaction.Id, tenantId, recipientSeller, amount: 50m);
        var primaryTransfer = BuildPaidSplitTransfer(transaction.Id, tenantId, primarySeller, amount: 50m, isPrimaryShare: true);

        var transfers = new List<SplitTransfer> { recipientTransfer, primaryTransfer };
        _splitTransferRepo.GetByTransactionIdAsync(tenantId, transaction.Id)
            .Returns(transfers);

        // Act — Partial refund of 60
        await InvokeReverseSplitsProportionallyAsync(transaction, 60m);

        // Assert — Both recipient and primary seller get proportional reversal
        // proportionalRefund = 60 * (100/100) = 60, each gets Round(60 * 50 / 100) = 30
        await _ledgerService.Received(1).ReturnToClearingAsync(
            tenantId, recipientSeller, 30m, Arg.Any<string>(), Arg.Any<string>());
        await _ledgerService.Received(1).ReturnToClearingAsync(
            tenantId, primarySeller, 30m, Arg.Any<string>(), Arg.Any<string>());
        await _ledgerService.Received(1).DrainClearingForRefundAsync(
            tenantId, 60m, Arg.Any<string>(), Arg.Any<string>());

        recipientTransfer.Status.Should().Be(SplitTransferStatus.PARTIALLY_REVERSED);
        primaryTransfer.Status.Should().Be(SplitTransferStatus.PARTIALLY_REVERSED);
    }

    [Fact]
    public async Task Refund_WhenTransferStatusIsNotReversible_ShouldSkipWithoutLedgerCorruption()
    {
        // Arrange: one transfer PAID (reversible), one FAILED (not reversible — filtered out)
        var tenantId = Guid.NewGuid();
        var sellerPaid = Guid.NewGuid();
        var sellerFailed = Guid.NewGuid();

        var transaction = BuildTransaction(tenantId, amount: 100m, netAmount: 100m);

        var paidTransfer = BuildPaidSplitTransfer(transaction.Id, tenantId, sellerPaid, amount: 50m);
        var failedTransfer = BuildFailedSplitTransfer(transaction.Id, tenantId, sellerFailed, amount: 50m);

        var transfers = new List<SplitTransfer> { paidTransfer, failedTransfer };
        _splitTransferRepo.GetByTransactionIdAsync(tenantId, transaction.Id)
            .Returns(transfers);

        // Act — Full refund
        await InvokeReverseSplitsProportionallyAsync(transaction, 100m);

        // Assert — Only the PAID transfer is reversed; FAILED is excluded by filter
        await _ledgerService.Received(1).ReturnToClearingAsync(
            tenantId, sellerPaid, 50m, Arg.Any<string>(), Arg.Any<string>());
        await _ledgerService.DidNotReceive().ReturnToClearingAsync(
            tenantId, sellerFailed, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());

        // DrainClearingForRefundAsync called with only the paid transfer's amount
        await _ledgerService.Received(1).DrainClearingForRefundAsync(
            tenantId, 50m, Arg.Any<string>(), Arg.Any<string>());

        paidTransfer.Status.Should().Be(SplitTransferStatus.REVERSED);
        paidTransfer.RemainingAmount.Should().Be(0m);

        // FAILED transfer remains untouched
        failedTransfer.Status.Should().Be(SplitTransferStatus.FAILED);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task InvokeReverseSplitsProportionallyAsync(Transaction transaction, decimal refundDelta)
    {
        var method = typeof(WebhooksService).GetMethod(
            "ReverseSplitsProportionallyAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().NotBeNull("ReverseSplitsProportionallyAsync must exist as a private method");

        var task = (Task)method!.Invoke(_sut, new object[] { transaction, refundDelta })!;
        await task;
    }

    private static Transaction BuildTransaction(Guid tenantId, decimal amount, decimal netAmount)
    {
        var result = Transaction.Create(
            tenantId: tenantId,
            amount: amount,
            paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: amount - netAmount,
            netAmount: netAmount,
            expectedSettlementDate: null,
            providerTxId: $"pi_{Guid.NewGuid():N}",
            sellerId: Guid.NewGuid());

        var transaction = result.Value;
        transaction.UpdateStatus(TransactionStatus.CAPTURED);
        return transaction;
    }

    private static SplitTransfer BuildPaidSplitTransfer(
        Guid transactionId, Guid tenantId, Guid recipientSellerId,
        decimal amount, bool isPrimaryShare = false)
    {
        var transfer = SplitTransfer.Create(transactionId, tenantId, recipientSellerId, amount, isPrimaryShare: isPrimaryShare).Value;
        transfer.Reserve();
        transfer.MarkProcessing();
        transfer.MarkPaid();
        return transfer;
    }

    private static SplitTransfer BuildPartiallyReversedSplitTransfer(
        Guid transactionId, Guid tenantId, Guid recipientSellerId,
        decimal amount, decimal reversedAmount)
    {
        var transfer = BuildPaidSplitTransfer(transactionId, tenantId, recipientSellerId, amount);
        transfer.PartialReverse(reversedAmount);
        return transfer;
    }

    private static SplitTransfer BuildFailedSplitTransfer(
        Guid transactionId, Guid tenantId, Guid recipientSellerId, decimal amount)
    {
        var transfer = SplitTransfer.Create(transactionId, tenantId, recipientSellerId, amount).Value;
        transfer.Reserve();
        transfer.MarkProcessing();
        transfer.Fail("Provider rejected transfer");
        return transfer;
    }
}
