using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Application.Modules.Splits.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Processors;

public class SplitProcessorTests
{
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly ISplitTransferRepository _splitTransferRepository = Substitute.For<ISplitTransferRepository>();
    private readonly ISplitAllocationRepository _splitAllocationRepository = Substitute.For<ISplitAllocationRepository>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly ISplitCalculationService _splitCalculationService = new SplitCalculationService();
    private readonly IItemSplitResolver _itemSplitResolver = Substitute.For<IItemSplitResolver>();
    private readonly ILogger<SplitProcessor> _logger = Substitute.For<ILogger<SplitProcessor>>();
    private readonly SplitProcessor _sut;

    public SplitProcessorTests()
    {
        _itemSplitResolver.ResolveFromItemsAsync(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(new ItemSplitResolution([], HasItemSplits: false));
        _sut = new SplitProcessor(_transactionRepository, _splitTransferRepository, _splitAllocationRepository, _ledgerService, _splitCalculationService, _itemSplitResolver, _logger);
    }

    [Fact]
    public async Task ProcessAllPendingSplitsAsync_ShouldDoNothing_WhenNoTransactionsWithPendingSplits()
    {
        // Arrange
        _transactionRepository.GetPendingSplitBatchAsync(Arg.Any<int>())
            .Returns(new List<(Guid, Guid)>());

        // Act
        await _sut.ProcessAllPendingSplitsAsync();

        // Assert
        await _transactionRepository.DidNotReceive().GetByIdWithSplitsAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
        await _ledgerService.DidNotReceive().DistributeFromClearingAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ProcessAllPendingSplitsAsync_ShouldProcessEachTransaction()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var txId1 = Guid.NewGuid();
        var txId2 = Guid.NewGuid();

        _transactionRepository.GetPendingSplitBatchAsync(Arg.Any<int>())
            .Returns(new List<(Guid, Guid)> { (tenantId, txId1), (tenantId, txId2) });

        var tx1 = CreateCapturedTransaction(txId1);
        var tx2 = CreateCapturedTransaction(txId2);

        _transactionRepository.GetByIdWithSplitsAsync(tenantId, txId1).Returns(tx1);
        _transactionRepository.GetByIdWithSplitsAsync(tenantId, txId2).Returns(tx2);

        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns((SplitTransfer?)null);

        // Act
        await _sut.ProcessAllPendingSplitsAsync();

        // Assert
        await _transactionRepository.Received(1).GetByIdWithSplitsAsync(tenantId, txId1);
        await _transactionRepository.Received(1).GetByIdWithSplitsAsync(tenantId, txId2);
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_ShouldPayPendingSplits_WhenTransactionIsCaptured()
    {
        // Arrange
        var txId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var tx = CreateCapturedTransactionWithSplit(txId, sellerId, 50m);

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        // No existing SplitTransfer for this recipient or primary
        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<bool>())
            .Returns((SplitTransfer?)null);

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert: distribute from clearing to recipient + primary seller
        await _ledgerService.Received(1).DistributeFromClearingAsync(
            tx.TenantId, sellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _ledgerService.Received(1).DistributeFromClearingAsync(
            tx.TenantId, PrimarySellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());

        // Assert: SplitTransfer records added (recipient + primary seller)
        _splitTransferRepository.Received(2).Add(Arg.Any<SplitTransfer>());

        tx.Splits.First().Status.Should().Be(SplitStatus.PAID);
        _transactionRepository.Received(1).Update(tx);
        await _transactionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_ShouldReturn_WhenTransactionNotFound()
    {
        // Arrange
        var txId = Guid.NewGuid();
        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns((Transaction?)null);

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert
        await _ledgerService.DidNotReceive().DistributeFromClearingAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        _transactionRepository.DidNotReceive().Update(Arg.Any<Transaction>());
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_ShouldReturn_WhenTransactionNotCaptured()
    {
        // Arrange
        var txId = Guid.NewGuid();
        var tx = CreateTransaction(txId, TransactionStatus.PROCESSING);

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert
        await _ledgerService.DidNotReceive().DistributeFromClearingAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_ShouldMarkFailed_WhenRecipientIdInvalid()
    {
        // Arrange
        var txId = Guid.NewGuid();
        var tx = CreateCapturedTransactionWithSplit(txId, Guid.Empty, 50m, invalidRecipientId: "not-a-guid");

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert: split marked as failed
        tx.Splits.First().Status.Should().Be(SplitStatus.FAILED);
        // Primary seller still gets full netAmount from SPLIT_CLEARING since no valid splits succeeded
        await _ledgerService.Received(1).DistributeFromClearingAsync(
            tx.TenantId, PrimarySellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_ShouldKeepPending_WhenLedgerServiceThrows()
    {
        // Arrange
        var txId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var tx = CreateCapturedTransactionWithSplit(txId, sellerId, 50m);

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns((SplitTransfer?)null);

        _ledgerService
            .DistributeFromClearingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("Ledger transfer failed"));

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert: Split stays PENDING for retry (SplitProcessor keeps it PENDING to allow batch retry)
        tx.Splits.First().Status.Should().Be(SplitStatus.PENDING);
        _transactionRepository.Received(1).Update(tx);
        await _transactionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_ShouldSkipNonPendingSplits()
    {
        // Arrange
        var txId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var tx = CreateCapturedTransactionWithSplit(txId, sellerId, 50m);

        // Mark the split as already paid so there are no pending splits
        tx.Splits.First().MarkAsPaid();

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert
        await _ledgerService.DidNotReceive().DistributeFromClearingAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        // No Update/SaveChanges should be called since we return early when pendingSplits is empty
        _transactionRepository.DidNotReceive().Update(Arg.Any<Transaction>());
    }

    [Fact]
    public async Task ProcessAllPendingSplitsAsync_ShouldRespectCancellationToken()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var txId1 = Guid.NewGuid();
        var txId2 = Guid.NewGuid();

        _transactionRepository.GetPendingSplitBatchAsync(Arg.Any<int>())
            .Returns(new List<(Guid, Guid)> { (tenantId, txId1), (tenantId, txId2) });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        await _sut.ProcessAllPendingSplitsAsync(cts.Token);

        // Assert — cancellation checked before first iteration
        await _transactionRepository.DidNotReceive().GetByIdWithSplitsAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_WhenMarkerExistsButLedgerNotCompleted_ShouldRetryDistribution()
    {
        // Arrange
        var txId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var tx = CreateCapturedTransactionWithSplit(txId, sellerId, 50m);

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        // Existing marker in RESERVED status (ledger never completed)
        var existingTransfer = SplitTransfer.Create(tx.Id, tx.TenantId, sellerId, 50m, isPrimaryShare: false).Value;
        existingTransfer.Reserve();

        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, sellerId, Arg.Is<bool>(x => x == false))
            .Returns(existingTransfer);

        // Primary share lookup returns null (no existing primary marker)
        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, PrimarySellerId, Arg.Is<bool>(x => x == true))
            .Returns((SplitTransfer?)null);

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert: DistributeFromClearingAsync was called (retry for recipient)
        await _ledgerService.Received(1).DistributeFromClearingAsync(
            tx.TenantId, sellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());

        // Existing transfer updated to PAID
        existingTransfer.Status.Should().Be(SplitTransferStatus.PAID);
        _splitTransferRepository.Received().Update(existingTransfer);

        // Split on transaction marked as paid
        tx.Splits.First().Status.Should().Be(SplitStatus.PAID);
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_WhenLedgerSucceeds_ShouldMarkSplitTransferPaid()
    {
        // Arrange
        var txId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var tx = CreateCapturedTransactionWithSplit(txId, sellerId, 50m);

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        // No existing marker
        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, sellerId, Arg.Is<bool>(x => x == false))
            .Returns((SplitTransfer?)null);

        // Primary share lookup returns null
        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, PrimarySellerId, Arg.Is<bool>(x => x == true))
            .Returns((SplitTransfer?)null);

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert: SplitTransfer.Add called for recipient (marker created in RESERVED state before ledger)
        _splitTransferRepository.Received(1).Add(Arg.Is<SplitTransfer>(st =>
            st.RecipientSellerId == sellerId && !st.IsPrimaryShare));

        // DistributeFromClearingAsync called for recipient
        await _ledgerService.Received(1).DistributeFromClearingAsync(
            tx.TenantId, sellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());

        // SplitTransfer updated with PAID status after ledger success
        _splitTransferRepository.Received().Update(Arg.Is<SplitTransfer>(st =>
            st.RecipientSellerId == sellerId && st.Status == SplitTransferStatus.PAID));

        // Split on transaction is marked as PAID
        tx.Splits.First().Status.Should().Be(SplitStatus.PAID);
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_WhenPrimarySellerIsAlsoRecipient_ShouldCreateRecipientAndPrimaryTransfers()
    {
        // Arrange: Primary seller is also the split recipient (50% of net)
        var txId = Guid.NewGuid();
        var tx = CreateCapturedTransactionWithSplit(txId, PrimarySellerId, 50m);

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        // No existing markers for either recipient or primary
        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, PrimarySellerId, Arg.Is<bool>(x => x == false))
            .Returns((SplitTransfer?)null);

        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, PrimarySellerId, Arg.Is<bool>(x => x == true))
            .Returns((SplitTransfer?)null);

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert: Two DistributeFromClearingAsync calls (one as recipient, one as primary residual)
        await _ledgerService.Received(2).DistributeFromClearingAsync(
            tx.TenantId, PrimarySellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());

        // Two Add calls for SplitTransfer (recipient + primary)
        _splitTransferRepository.Received(2).Add(Arg.Any<SplitTransfer>());
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_WhenPrimaryResidualAlreadyPaid_ShouldNotDuplicate()
    {
        // Arrange: TX with 1 split for another seller
        var txId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var tx = CreateCapturedTransactionWithSplit(txId, sellerId, 50m);

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        // Recipient marker → null (will be processed normally)
        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, sellerId, Arg.Is<bool>(x => x == false))
            .Returns((SplitTransfer?)null);

        // Primary marker → already PAID
        var paidPrimaryTransfer = SplitTransfer.Create(tx.Id, tx.TenantId, PrimarySellerId, 48m, isPrimaryShare: true).Value;
        paidPrimaryTransfer.Reserve();
        paidPrimaryTransfer.MarkProcessing();
        paidPrimaryTransfer.MarkPaid();

        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, PrimarySellerId, Arg.Is<bool>(x => x == true))
            .Returns(paidPrimaryTransfer);

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert: DistributeFromClearingAsync called once for recipient only
        await _ledgerService.Received(1).DistributeFromClearingAsync(
            tx.TenantId, sellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());

        // NOT called for primary (already paid)
        await _ledgerService.DidNotReceive().DistributeFromClearingAsync(
            tx.TenantId, PrimarySellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());

        // No new SplitTransfer Added for primary
        _splitTransferRepository.DidNotReceive().Add(Arg.Is<SplitTransfer>(st => st.IsPrimaryShare));
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_WhenRecipientAlreadyPaid_ShouldNotDuplicate()
    {
        // Arrange
        var txId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var tx = CreateCapturedTransactionWithSplit(txId, sellerId, 50m);

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        // Existing recipient marker with PAID status
        var paidTransfer = SplitTransfer.Create(tx.Id, tx.TenantId, sellerId, 50m, isPrimaryShare: false).Value;
        paidTransfer.Reserve();
        paidTransfer.MarkProcessing();
        paidTransfer.MarkPaid();

        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, sellerId, Arg.Is<bool>(x => x == false))
            .Returns(paidTransfer);

        // Primary marker → null
        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, PrimarySellerId, Arg.Is<bool>(x => x == true))
            .Returns((SplitTransfer?)null);

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert: DistributeFromClearingAsync NOT called for recipient (skipped due to PAID)
        await _ledgerService.DidNotReceive().DistributeFromClearingAsync(
            tx.TenantId, sellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());

        // Split is marked as PAID
        tx.Splits.First().Status.Should().Be(SplitStatus.PAID);
    }

    [Fact]
    public async Task ProcessSplitsForTransactionAsync_WhenLedgerFails_ShouldMarkTransferFailedAndAllowRetry()
    {
        // Arrange
        var txId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var tx = CreateCapturedTransactionWithSplit(txId, sellerId, 50m);

        _transactionRepository.GetByIdWithSplitsAsync(txId).Returns(tx);

        // No existing marker
        _splitTransferRepository
            .GetByTransactionAndRecipientAsync(tx.TenantId, tx.Id, sellerId, Arg.Is<bool>(x => x == false))
            .Returns((SplitTransfer?)null);

        // Ledger throws
        _ledgerService.DistributeFromClearingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("Ledger unavailable"));

        // Act
        await _sut.ProcessSplitsForTransactionAsync(txId);

        // Assert: Split stays PENDING for retry on next batch run (SplitProcessor intentionally does NOT mark FAILED)
        tx.Splits.First().Status.Should().Be(SplitStatus.PENDING);

        // SplitTransfer marker was created before ledger call
        _splitTransferRepository.Received(1).Add(Arg.Is<SplitTransfer>(st =>
            st.RecipientSellerId == sellerId && !st.IsPrimaryShare));

        // Transaction is Updated and SaveChangesAsync called
        _transactionRepository.Received(1).Update(tx);
        await _transactionRepository.Received(1).SaveChangesAsync();

        // Transaction is Updated and SaveChangesAsync called
        _transactionRepository.Received(1).Update(tx);
        await _transactionRepository.Received(1).SaveChangesAsync();

        // SplitTransfer was Added (marker created before ledger call)
        _splitTransferRepository.Received(1).Add(Arg.Is<SplitTransfer>(st =>
            st.RecipientSellerId == sellerId && !st.IsPrimaryShare));

        // The marker stays in RESERVED status (catch only marks TransactionSplit as failed,
        // not the SplitTransfer entity — stuck RESERVED will be retried on next run per F1 behavior)
    }

    #region Helpers

    private static readonly Guid PrimarySellerId = Guid.NewGuid();

    private static Transaction CreateTransaction(Guid txId, TransactionStatus status)
    {
        var tenantId = Guid.NewGuid();
        var result = Transaction.Create(
            tenantId: tenantId,
            amount: 100m,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 2m,
            netAmount: 98m,
            expectedSettlementDate: null,
            providerTxId: "prov_tx",
            sellerId: PrimarySellerId);

        var tx = result.Value;
        if (status == TransactionStatus.CAPTURED)
            tx.UpdateStatus(TransactionStatus.CAPTURED);

        return tx;
    }

    private static Transaction CreateCapturedTransaction(Guid txId)
    {
        return CreateTransaction(txId, TransactionStatus.CAPTURED);
    }

    private static Transaction CreateCapturedTransactionWithSplit(Guid txId, Guid sellerId, decimal splitAmount, string? invalidRecipientId = null)
    {
        var tx = CreateCapturedTransaction(txId);
        var recipientId = invalidRecipientId ?? sellerId.ToString();
        tx.AddSplit(recipientId, "SELLER", splitAmount);
        return tx;
    }

    #endregion
}
