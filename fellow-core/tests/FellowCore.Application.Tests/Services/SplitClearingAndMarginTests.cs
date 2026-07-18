using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Ledgers.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class SplitClearingAndMarginTests
{
    private readonly ILedgerRepository _ledgerRepository = Substitute.For<ILedgerRepository>();
    private readonly LedgerService _sut;
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    public SplitClearingAndMarginTests()
    {
        _sut = new LedgerService(_ledgerRepository, Substitute.For<ILogger<LedgerService>>());
    }

    // ── CreditSplitClearingAsync ─────────────────────────────────────────

    [Fact]
    public async Task CreditSplitClearingAsync_ShouldCreditPlatformReceivableAndMoveToClearingAccount()
    {
        // Arrange
        var receivable = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_RECEIVABLE);
        var clearing = LedgerAccount.Create(TenantId, null, LedgerAccountType.SPLIT_CLEARING);

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_RECEIVABLE)
            .Returns(receivable);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.SPLIT_CLEARING)
            .Returns(clearing);

        // Act
        await _sut.CreditSplitClearingAsync(TenantId, 1000m, "Test split clearing", "tx_123");

        // Assert: entries created (credit receivable, debit receivable, credit clearing)
        _ledgerRepository.Received().AddEntry(Arg.Any<LedgerEntry>());
        await _ledgerRepository.Received().SaveChangesAsync();

        // SPLIT_CLEARING should have a positive balance
        clearing.Balance.Should().Be(1000m);
    }

    [Fact]
    public async Task CreditSplitClearingAsync_WithZeroAmount_ShouldDoNothing()
    {
        // Act & Assert: should not throw, should not call repo
        await _sut.CreditSplitClearingAsync(TenantId, 0m, "Zero", "tx_000");

        await _ledgerRepository.DidNotReceive().SaveChangesAsync();
    }

    // ── DistributeFromClearingAsync ──────────────────────────────────────

    [Fact]
    public async Task DistributeFromClearingAsync_ShouldDebitClearingAndCreditSellerWallet()
    {
        // Arrange
        var clearing = LedgerAccount.Create(TenantId, null, LedgerAccountType.SPLIT_CLEARING);
        clearing.Credit(500m, "Seed", "TEST", "seed");

        var wallet = LedgerAccount.Create(TenantId, SellerId, LedgerAccountType.WALLET);

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.SPLIT_CLEARING)
            .Returns(clearing);
        _ledgerRepository.GetAccountAsync(TenantId, LedgerAccountType.WALLET, SellerId)
            .Returns(wallet);

        // Act
        await _sut.DistributeFromClearingAsync(TenantId, SellerId, 300m, "Split distribution", "tx_123");

        // Assert
        clearing.Balance.Should().Be(200m); // 500 - 300
        wallet.Balance.Should().Be(300m);
        await _ledgerRepository.Received().SaveChangesAsync();
    }

    [Fact]
    public async Task DistributeFromClearingAsync_ShouldCreateSellerWalletIfNotExists()
    {
        // Arrange
        var clearing = LedgerAccount.Create(TenantId, null, LedgerAccountType.SPLIT_CLEARING);
        clearing.Credit(200m, "Seed", "TEST", "seed");

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.SPLIT_CLEARING)
            .Returns(clearing);
        _ledgerRepository.GetAccountAsync(TenantId, LedgerAccountType.WALLET, SellerId)
            .Returns((LedgerAccount?)null);

        // Act
        await _sut.DistributeFromClearingAsync(TenantId, SellerId, 200m, "Split dist", "tx_456");

        // Assert: wallet created via AddAccount
        _ledgerRepository.Received().AddAccount(Arg.Is<LedgerAccount>(a =>
            a.SellerId == SellerId && a.Type == LedgerAccountType.WALLET));
        await _ledgerRepository.Received().SaveChangesAsync();
    }

    // ── ReturnToClearingAsync ────────────────────────────────────────────

    [Fact]
    public async Task ReturnToClearingAsync_ShouldDebitSellerWalletAndCreditClearing()
    {
        // Arrange
        var wallet = LedgerAccount.Create(TenantId, SellerId, LedgerAccountType.WALLET);
        wallet.Credit(500m, "Seed", "TEST", "seed");

        var clearing = LedgerAccount.Create(TenantId, null, LedgerAccountType.SPLIT_CLEARING);

        _ledgerRepository.GetAccountAsync(TenantId, LedgerAccountType.WALLET, SellerId)
            .Returns(wallet);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.SPLIT_CLEARING)
            .Returns(clearing);

        // Act
        await _sut.ReturnToClearingAsync(TenantId, SellerId, 300m, "Refund reversal", "tx_789");

        // Assert
        wallet.Balance.Should().Be(200m); // 500 - 300
        clearing.Balance.Should().Be(300m);
        await _ledgerRepository.Received().SaveChangesAsync();
    }

    // ── DrainClearingForRefundAsync ──────────────────────────────────────

    [Fact]
    public async Task DrainClearingForRefundAsync_ShouldDebitClearingAndCreditPlatformPayout()
    {
        // Arrange
        var clearing = LedgerAccount.Create(TenantId, null, LedgerAccountType.SPLIT_CLEARING);
        clearing.Credit(400m, "Seed", "TEST", "seed");

        var payout = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_PAYOUT);

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.SPLIT_CLEARING)
            .Returns(clearing);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_PAYOUT)
            .Returns(payout);

        // Act
        await _sut.DrainClearingForRefundAsync(TenantId, 400m, "Refund drain", "tx_abc");

        // Assert
        clearing.Balance.Should().Be(0m);
        payout.Balance.Should().Be(400m);
        await _ledgerRepository.Received().SaveChangesAsync();
    }

    // ── Full split clearing lifecycle ────────────────────────────────────

    [Fact]
    public async Task FullSplitClearingLifecycle_CaptureDistributeRefund_ShouldBalanceToZero()
    {
        // Arrange: shared accounts
        var receivable = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_RECEIVABLE);
        var clearing = LedgerAccount.Create(TenantId, null, LedgerAccountType.SPLIT_CLEARING);
        var payout = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_PAYOUT);

        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();
        var wallet1 = LedgerAccount.Create(TenantId, seller1, LedgerAccountType.WALLET);
        var wallet2 = LedgerAccount.Create(TenantId, seller2, LedgerAccountType.WALLET);

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_RECEIVABLE).Returns(receivable);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.SPLIT_CLEARING).Returns(clearing);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_PAYOUT).Returns(payout);
        _ledgerRepository.GetAccountAsync(TenantId, LedgerAccountType.WALLET, seller1).Returns(wallet1);
        _ledgerRepository.GetAccountAsync(TenantId, LedgerAccountType.WALLET, seller2).Returns(wallet2);

        // Act 1: Capture — 1000 net goes to SPLIT_CLEARING
        await _sut.CreditSplitClearingAsync(TenantId, 1000m, "Capture", "tx_1");

        clearing.Balance.Should().Be(1000m);

        // Act 2: Distribute — 600 to seller1, 400 to seller2
        await _sut.DistributeFromClearingAsync(TenantId, seller1, 600m, "Split to S1", "tx_1");
        await _sut.DistributeFromClearingAsync(TenantId, seller2, 400m, "Split to S2", "tx_1");

        clearing.Balance.Should().Be(0m);
        wallet1.Balance.Should().Be(600m);
        wallet2.Balance.Should().Be(400m);

        // Act 3: Full refund — return all splits to clearing, then drain
        await _sut.ReturnToClearingAsync(TenantId, seller1, 600m, "Refund S1", "tx_1");
        await _sut.ReturnToClearingAsync(TenantId, seller2, 400m, "Refund S2", "tx_1");

        clearing.Balance.Should().Be(1000m);
        wallet1.Balance.Should().Be(0m);
        wallet2.Balance.Should().Be(0m);

        await _sut.DrainClearingForRefundAsync(TenantId, 1000m, "Drain for refund", "tx_1");

        // Assert: everything balanced
        clearing.Balance.Should().Be(0m);
        payout.Balance.Should().Be(1000m);
    }

    // ── Negative Margin ──────────────────────────────────────────────────

    [Fact]
    public async Task RecordPlatformMarginAsync_NegativeMargin_ShouldDebitPlatformMarginAccount()
    {
        // Arrange: provider cost (5.00) exceeds platform fee (3.00) → margin = -2.00
        var feeAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_FEE);
        var costAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PROVIDER_COST);
        var marginAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_MARGIN);

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_FEE).Returns(feeAccount);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PROVIDER_COST).Returns(costAccount);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_MARGIN).Returns(marginAccount);

        // Act
        await _sut.RecordPlatformMarginAsync(TenantId, 3m, 5m, "TX with negative margin", "tx_neg");

        // Assert:
        // PLATFORM_FEE: +3 (credit) -5 (debit to cost) +2 (credit from margin deficit) = 0
        feeAccount.Balance.Should().Be(0m);
        // PROVIDER_COST: +5 (credit)
        costAccount.Balance.Should().Be(5m);
        // PLATFORM_MARGIN: -2 (debit, negative = platform subsidy)
        marginAccount.Balance.Should().Be(-2m);
    }

    [Fact]
    public async Task RecordPlatformMarginAsync_PositiveMargin_ShouldCreditPlatformMarginAccount()
    {
        // Arrange: platform fee = 10, provider cost = 4 → margin = 6
        var feeAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_FEE);
        var costAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PROVIDER_COST);
        var marginAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_MARGIN);

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_FEE).Returns(feeAccount);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PROVIDER_COST).Returns(costAccount);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_MARGIN).Returns(marginAccount);

        // Act
        await _sut.RecordPlatformMarginAsync(TenantId, 10m, 4m, "TX with positive margin", "tx_pos");

        // Assert:
        // PLATFORM_FEE: +10 -4 -6 = 0
        feeAccount.Balance.Should().Be(0m);
        // PROVIDER_COST: +4
        costAccount.Balance.Should().Be(4m);
        // PLATFORM_MARGIN: +6
        marginAccount.Balance.Should().Be(6m);
    }

    [Fact]
    public async Task RecordPlatformMarginAsync_ZeroMargin_ShouldNotCreateMarginEntries()
    {
        // Arrange: fee == cost → margin = 0
        var feeAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_FEE);
        var costAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PROVIDER_COST);

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_FEE).Returns(feeAccount);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PROVIDER_COST).Returns(costAccount);
        // PLATFORM_MARGIN should not be requested
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_MARGIN)
            .Returns((LedgerAccount?)null);

        // Act
        await _sut.RecordPlatformMarginAsync(TenantId, 7m, 7m, "TX with zero margin", "tx_zero");

        // Assert: fee zeroed out, cost credited, no margin account touched
        feeAccount.Balance.Should().Be(0m);
        costAccount.Balance.Should().Be(7m);
    }

    [Fact]
    public async Task ReversePlatformMarginAsync_NegativeMargin_ShouldCreditPlatformMarginAccount()
    {
        // Arrange: reverse a transaction that had negative margin (fee=3, cost=5, margin=-2)
        // After RecordPlatformMarginAsync: FEE=0, COST=5, MARGIN=-2
        var feeAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_FEE);
        var costAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PROVIDER_COST);
        costAccount.Credit(5m, "Seed", "TEST", "seed");
        var marginAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_MARGIN);
        // PLATFORM_MARGIN at -2 (simulated via ForceDebit from 0)
        marginAccount.ForceDebit(2m, "Seed negative margin", "TEST", "seed");

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_FEE).Returns(feeAccount);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PROVIDER_COST).Returns(costAccount);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_MARGIN).Returns(marginAccount);

        // Act
        await _sut.ReversePlatformMarginAsync(TenantId, 3m, 5m, "Reversal negative margin TX", "tx_neg");

        // Assert: all accounts return to 0
        // FEE: ForceDebit(-3) + Credit(+3 from COST) = 0
        feeAccount.Balance.Should().Be(0m);
        // COST: 5 - 3 (fee reversal) - 2 (margin reversal) = 0
        costAccount.Balance.Should().Be(0m);
        // MARGIN: -2 + 2 (credit reversal) = 0
        marginAccount.Balance.Should().Be(0m);
    }

    // ── RecordCostAdjustmentAsync ────────────────────────────────────────

    [Fact]
    public async Task RecordCostAdjustmentAsync_PositiveAdjustment_ShouldIncreaseCostDecreaseMargin()
    {
        // Arrange: actual cost higher than estimated by 1.50
        var costAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PROVIDER_COST);
        costAccount.Credit(4m, "Seed", "TEST", "seed");
        var marginAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_MARGIN);
        marginAccount.Credit(6m, "Seed", "TEST", "seed");

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PROVIDER_COST).Returns(costAccount);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_MARGIN).Returns(marginAccount);

        // Act
        await _sut.RecordCostAdjustmentAsync(TenantId, 1.5m, "Cost adj", "tx_adj");

        // Assert
        costAccount.Balance.Should().Be(5.5m); // 4 + 1.5
        marginAccount.Balance.Should().Be(4.5m); // 6 - 1.5
    }

    [Fact]
    public async Task RecordCostAdjustmentAsync_NegativeAdjustment_ShouldDecreaseCostIncreaseMargin()
    {
        // Arrange: actual cost lower than estimated by 0.80
        var costAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PROVIDER_COST);
        costAccount.Credit(4m, "Seed", "TEST", "seed");
        var marginAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_MARGIN);
        marginAccount.Credit(6m, "Seed", "TEST", "seed");

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PROVIDER_COST).Returns(costAccount);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_MARGIN).Returns(marginAccount);

        // Act
        await _sut.RecordCostAdjustmentAsync(TenantId, -0.8m, "Cost adj negative", "tx_adj2");

        // Assert
        costAccount.Balance.Should().Be(3.2m); // 4 - 0.8
        marginAccount.Balance.Should().Be(6.8m); // 6 + 0.8
    }

    [Fact]
    public async Task RecordCostAdjustmentAsync_LargePositiveAdjustment_ShouldMakeMarginNegative()
    {
        // Arrange: cost adjustment exceeds current margin → margin goes negative
        var costAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PROVIDER_COST);
        costAccount.Credit(4m, "Seed", "TEST", "seed");
        var marginAccount = LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_MARGIN);
        marginAccount.Credit(2m, "Seed small margin", "TEST", "seed");

        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PROVIDER_COST).Returns(costAccount);
        _ledgerRepository.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_MARGIN).Returns(marginAccount);

        // Act: adjustment of 5 > current margin of 2
        await _sut.RecordCostAdjustmentAsync(TenantId, 5m, "Large cost adj", "tx_adj3");

        // Assert: margin goes negative (platform subsidy)
        costAccount.Balance.Should().Be(9m); // 4 + 5
        marginAccount.Balance.Should().Be(-3m); // 2 - 5
    }

    [Fact]
    public async Task RecordCostAdjustmentAsync_ZeroAdjustment_ShouldDoNothing()
    {
        await _sut.RecordCostAdjustmentAsync(TenantId, 0m, "No adjustment", "tx_zero");

        await _ledgerRepository.DidNotReceive().SaveChangesAsync();
    }
}
