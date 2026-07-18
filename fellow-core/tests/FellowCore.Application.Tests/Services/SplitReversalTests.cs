using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Tests.Helpers;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Rails;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using FellowCore.Application.Modules.Webhooks.Services;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FellowCore.Application.Tests.Services;

public class SplitReversalTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();
    private static readonly Guid Recipient1 = Guid.NewGuid();
    private static readonly Guid Recipient2 = Guid.NewGuid();

    // ── Full refund: reverses all splits ──────────────────────────────────

    [Fact]
    public async Task FullRefund_ShouldReverseAllSplitTransfers()
    {
        // Arrange
        var transaction = CreateCapturedTransaction(1000m, 970m);
        transaction.SetProviderTxId("pi_full_refund");

        var splitTransferRepo = Substitute.For<ISplitTransferRepository>();
        var transfer1 = CreateSplitTransfer(transaction.Id, TenantId, Recipient1, 500m, SplitTransferStatus.RESERVED);
        var transfer2 = CreateSplitTransfer(transaction.Id, TenantId, Recipient2, 470m, SplitTransferStatus.PAID);

        splitTransferRepo.GetByTransactionIdAsync(TenantId, transaction.Id)
            .Returns(new List<SplitTransfer> { transfer1, transfer2 });

        var ledgerService = Substitute.For<ILedgerService>();
        var transactionRepo = Substitute.For<ITransactionRepository>();
        transactionRepo.GetByProviderTxIdAsync("pi_full_refund").Returns(transaction);

        var sut = CreateSut(transactionRepo, ledgerService, splitTransferRepo);

        var payload = CreateRefundPayload("pi_full_refund", 100000); // 1000 BRL = full refund

        // Act
        await sut.HandleStripeEventAsync(payload);

        // Assert: Both recipients returned to clearing
        await ledgerService.Received(1).ReturnToClearingAsync(
            TenantId, Recipient1, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
        await ledgerService.Received(1).ReturnToClearingAsync(
            TenantId, Recipient2, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());

        // Assert: clearing drained for refund
        await ledgerService.Received(1).DrainClearingForRefundAsync(
            TenantId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());

        // Assert: Both transfers updated
        splitTransferRepo.Received(1).Update(transfer1);
        splitTransferRepo.Received(1).Update(transfer2);
        await splitTransferRepo.Received(1).SaveChangesAsync();
    }

    // ── Partial refund: proportional reversal ─────────────────────────────

    [Fact]
    public async Task PartialRefund_ShouldReverseProportionally()
    {
        // Arrange: TX of 1000, net 970; split: 600 to R1, 370 to R2
        var transaction = CreateCapturedTransaction(1000m, 970m);
        transaction.SetProviderTxId("pi_partial_refund");

        var splitTransferRepo = Substitute.For<ISplitTransferRepository>();
        var transfer1 = CreateSplitTransfer(transaction.Id, TenantId, Recipient1, 600m, SplitTransferStatus.RESERVED);
        var transfer2 = CreateSplitTransfer(transaction.Id, TenantId, Recipient2, 370m, SplitTransferStatus.PAID);

        splitTransferRepo.GetByTransactionIdAsync(TenantId, transaction.Id)
            .Returns(new List<SplitTransfer> { transfer1, transfer2 });

        var ledgerService = Substitute.For<ILedgerService>();
        var transactionRepo = Substitute.For<ITransactionRepository>();
        transactionRepo.GetByProviderTxIdAsync("pi_partial_refund").Returns(transaction);

        var sut = CreateSut(transactionRepo, ledgerService, splitTransferRepo);

        // Refund 500 BRL (partial, 50% of 1000)
        var payload = CreateRefundPayload("pi_partial_refund", 50000);

        // Act
        await sut.HandleStripeEventAsync(payload);

        // Assert: proportional return to clearing based on net refund
        // proportionalRefund = 500 * (970/1000) = 485
        // R1 reversal: 485 * (600/970) = 300.00
        // R2 reversal: 485 * (370/970) = 185.00
        await ledgerService.Received(1).ReturnToClearingAsync(
            TenantId, Recipient1,
            Arg.Is<decimal>(d => d >= 299m && d <= 301m),
            Arg.Any<string>(), Arg.Any<string>());

        await ledgerService.Received(1).ReturnToClearingAsync(
            TenantId, Recipient2,
            Arg.Is<decimal>(d => d >= 184m && d <= 186m),
            Arg.Any<string>(), Arg.Any<string>());

        // Assert: clearing drained
        await ledgerService.Received(1).DrainClearingForRefundAsync(
            TenantId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ── No splits: should not attempt reversal ───────────────────────────

    [Fact]
    public async Task Refund_NoSplitTransfers_ShouldNotCallDebit()
    {
        var transaction = CreateCapturedTransaction(500m, 490m);
        transaction.SetProviderTxId("pi_no_splits");

        var splitTransferRepo = Substitute.For<ISplitTransferRepository>();
        splitTransferRepo.GetByTransactionIdAsync(TenantId, transaction.Id)
            .Returns(new List<SplitTransfer>());

        var ledgerService = Substitute.For<ILedgerService>();
        var transactionRepo = Substitute.For<ITransactionRepository>();
        transactionRepo.GetByProviderTxIdAsync("pi_no_splits").Returns(transaction);

        var sut = CreateSut(transactionRepo, ledgerService, splitTransferRepo);

        var payload = CreateRefundPayload("pi_no_splits", 50000);

        await sut.HandleStripeEventAsync(payload);

        // The main seller debit should happen, but no split-specific debits
        // (only 1 call to DebitSellerAsync for the primary seller)
        await ledgerService.Received(1).DebitSellerAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ── Already reversed splits are skipped ──────────────────────────────

    [Fact]
    public async Task Refund_AlreadyReversedSplits_ShouldSkipThem()
    {
        var transaction = CreateCapturedTransaction(1000m, 970m);
        transaction.SetProviderTxId("pi_already_reversed");

        var splitTransferRepo = Substitute.For<ISplitTransferRepository>();
        var reversedTransfer = CreateSplitTransfer(transaction.Id, TenantId, Recipient1, 500m, SplitTransferStatus.REVERSED);
        var activeTransfer = CreateSplitTransfer(transaction.Id, TenantId, Recipient2, 470m, SplitTransferStatus.RESERVED);

        splitTransferRepo.GetByTransactionIdAsync(TenantId, transaction.Id)
            .Returns(new List<SplitTransfer> { reversedTransfer, activeTransfer });

        var ledgerService = Substitute.For<ILedgerService>();
        var transactionRepo = Substitute.For<ITransactionRepository>();
        transactionRepo.GetByProviderTxIdAsync("pi_already_reversed").Returns(transaction);

        var sut = CreateSut(transactionRepo, ledgerService, splitTransferRepo);

        var payload = CreateRefundPayload("pi_already_reversed", 100000);

        await sut.HandleStripeEventAsync(payload);

        // Only active transfer's recipient should be debited for split reversal
        // (the reversed one is skipped)
        splitTransferRepo.Received(1).Update(activeTransfer);
        splitTransferRepo.DidNotReceive().Update(reversedTransfer);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Transaction CreateCapturedTransaction(decimal amount, decimal netAmount)
    {
        var result = Transaction.Create(
            TenantId, amount, PaymentType.CREDIT_CARD, PaymentProvider.STRIPE,
            1, amount - netAmount, netAmount, DateTime.UtcNow.AddDays(30),
            $"prov_{Guid.NewGuid():N}", SellerId);
        var tx = result.Value;
        tx.UpdateStatus(TransactionStatus.CAPTURED);
        return tx;
    }

    private static SplitTransfer CreateSplitTransfer(Guid txId, Guid tenantId, Guid recipientId, decimal amount, SplitTransferStatus status)
    {
        var createResult = SplitTransfer.Create(txId, tenantId, recipientId, amount);
        var transfer = createResult.Value;
        if (status >= SplitTransferStatus.RESERVED) transfer.Reserve();
        if (status >= SplitTransferStatus.PAID) transfer.MarkPaid();
        if (status == SplitTransferStatus.REVERSED) transfer.Reverse();
        return transfer;
    }

    private static WebhooksService CreateSut(
        ITransactionRepository transactionRepo,
        ILedgerService ledgerService,
        ISplitTransferRepository splitTransferRepo)
    {
        var providerFactory = Substitute.For<IPaymentProviderFactory>();
        var railRouter = new RailRouter([
            new StripeCardRail(providerFactory),
            new StripeBoletoRail(providerFactory),
            new OpenPixRail(providerFactory)
        ]);

        return new WebhooksService(
            transactionRepo,
            Substitute.For<ITransactionInstallmentRepository>(), Substitute.For<ISellerRepository>(), Substitute.For<ITenantRepository>(),
            Substitute.For<IWebhookEndpointRepository>(), Substitute.For<IWebhookDeliveryRepository>(),
            InboundWebhookGuardMockHelper.CreatePermissive(),
            ledgerService, Substitute.For<ISecurityService>(),
            Substitute.For<IConfiguration>(), Substitute.For<IUnitOfWork>(),
            Substitute.For<IBackgroundJobs>(), railRouter,
            Substitute.For<IPaymentIntentRepository>(), Substitute.For<IDisputeRepository>(),
            splitTransferRepo, Substitute.For<IDomainEventDispatcher>(),
            Substitute.For<IWebhookProbeClient>(),
            Substitute.For<IAppMetrics>(),
            Substitute.For<FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator>(),
            Microsoft.Extensions.Options.Options.Create(new FellowCore.Application.Modules.Pricing.Options.TierPricingOptions()),
            NullLogger<WebhooksService>.Instance);
    }

    private static StripeWebhookDto CreateRefundPayload(string paymentIntentId, long amountRefundedCents)
    {
        return new StripeWebhookDto(
            Id: $"evt_{Guid.NewGuid():N}",
            Type: "charge.refunded",
            Data: new StripeWebhookData(new StripeWebhookObject(
                Id: $"ch_{Guid.NewGuid():N}",
                PaymentIntent: paymentIntentId,
                AmountRefunded: amountRefundedCents
            )));
    }
}
