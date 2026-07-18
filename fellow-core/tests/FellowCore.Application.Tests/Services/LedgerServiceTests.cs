using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Ledgers.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class LedgerServiceTests
{
    private readonly ILedgerRepository _ledgerRepository = Substitute.For<ILedgerRepository>();
    private readonly LedgerService _sut;

    public LedgerServiceTests()
    {
        _sut = new LedgerService(_ledgerRepository, Substitute.For<ILogger<LedgerService>>());
    }

    [Fact]
    public async Task DebitSellerAsync_ShouldThrowNotFoundException_WhenWalletNotFound()
    {
        _ledgerRepository.GetAccountAsync(Arg.Any<Guid>(), Arg.Any<LedgerAccountType>(), Arg.Any<Guid>())
            .Returns((LedgerAccount?)null);

        var act = () => _sut.DebitSellerAsync(Guid.NewGuid(), Guid.NewGuid(), 100m, "Payout", "payout-001");

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*WALLET*");
    }

    [Fact]
    public async Task DebitSellerAsync_ShouldThrowBusinessException_WhenInsufficientBalance()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var wallet = LedgerAccount.Create(tenantId, sellerId, LedgerAccountType.WALLET);
        _ = wallet.Credit(50m, "Saldo", "SEED", "seed");
        _ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.WALLET, sellerId)
            .Returns(wallet);

        var platformPayout = LedgerAccount.Create(tenantId, null, LedgerAccountType.PLATFORM_PAYOUT);
        _ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_PAYOUT)
            .Returns(platformPayout);

        var act = () => _sut.DebitSellerAsync(tenantId, sellerId, 200m, "Payout", "payout-001");

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*Saldo insuficiente*");
    }

    [Fact]
    public async Task DebitSellerAsync_ShouldDebitAndPersist_WhenBalanceSufficient()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var wallet = LedgerAccount.Create(tenantId, sellerId, LedgerAccountType.WALLET);
        _ = wallet.Credit(500m, "Saldo", "SEED", "seed");
        _ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.WALLET, sellerId)
            .Returns(wallet);

        // Platform payout account for double-entry
        var platformPayout = LedgerAccount.Create(tenantId, null, LedgerAccountType.PLATFORM_PAYOUT);
        _ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_PAYOUT)
            .Returns(platformPayout);

        var result = await _sut.DebitSellerAsync(tenantId, sellerId, 200m, "Payout", "payout-001");

        result.Balance.Should().Be(300m);
        _ledgerRepository.Received(2).AddEntry(Arg.Any<LedgerEntry>());
        await _ledgerRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task GetBalanceAsync_ShouldReturnCorrectBalances()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var wallet = LedgerAccount.Create(tenantId, sellerId, LedgerAccountType.WALLET);
        _ = wallet.Credit(300m, "Saldo wallet", "SEED", "s1");

        var future = LedgerAccount.Create(tenantId, sellerId, LedgerAccountType.FUTURE_RECEIVABLES);
        _ = future.Credit(700m, "Saldo futuro", "SEED", "s2");

        _ledgerRepository.GetAccountsBySellerAsync(tenantId, sellerId)
            .Returns([wallet, future]);

        var balance = await _sut.GetBalanceAsync(tenantId, sellerId);

        balance.Available.Should().Be(300m);
        balance.WaitingFunds.Should().Be(700m);
        balance.Total.Should().Be(1000m);
    }

    [Fact]
    public async Task TransferFundsAsync_ShouldNotTransfer_WhenNoFutureReceivables()
    {
        _ledgerRepository.GetAccountAsync(Arg.Any<Guid>(), LedgerAccountType.FUTURE_RECEIVABLES, Arg.Any<Guid>())
            .Returns((LedgerAccount?)null);

        await _sut.TransferFundsAsync(Guid.NewGuid(), Guid.NewGuid(), 100m);

        _ledgerRepository.DidNotReceive().UpdateAccount(Arg.Any<LedgerAccount>());
    }

    [Fact]
    public async Task RecordDirectChargeFundsAsync_ShouldCreateDoubleEntryWithExternalFunds()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var externalFunds = LedgerAccount.Create(tenantId, sellerId, LedgerAccountType.EXTERNAL_FUNDS);
        _ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.EXTERNAL_FUNDS, sellerId)
            .Returns(externalFunds);

        var sellerWallet = LedgerAccount.Create(tenantId, sellerId, LedgerAccountType.WALLET);
        _ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.WALLET, sellerId)
            .Returns(sellerWallet);

        var platformFee = LedgerAccount.Create(tenantId, null, LedgerAccountType.PLATFORM_FEE);
        _ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_FEE)
            .Returns(platformFee);

        await _sut.RecordDirectChargeFundsAsync(tenantId, sellerId, 95m, 5m, LedgerAccountType.WALLET, "Test direct charge");

        // 5 entries: external credit (100), external debit seller (95), seller credit (95),
        //            external debit fee (5), platform fee credit (5)
        _ledgerRepository.Received(5).AddEntry(Arg.Any<LedgerEntry>());

        // EXTERNAL_FUNDS: +100 -95 -5 = 0 (fully allocated)
        externalFunds.Balance.Should().Be(0m);
        sellerWallet.Balance.Should().Be(95m);
        platformFee.Balance.Should().Be(5m);

        // Seller credit and its contra-entry (external debit) should be linked
        var sellerEntry = sellerWallet.Entries.Last();
        sellerEntry.ContraEntryId.Should().NotBeNull();

        // Fee credit and its contra-entry (external debit) should be linked
        var feeEntry = platformFee.Entries.Last();
        feeEntry.ContraEntryId.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordDirectChargeFundsAsync_NoFee_ShouldSkipPlatformFeeEntries()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var externalFunds = LedgerAccount.Create(tenantId, sellerId, LedgerAccountType.EXTERNAL_FUNDS);
        _ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.EXTERNAL_FUNDS, sellerId)
            .Returns(externalFunds);

        var sellerWallet = LedgerAccount.Create(tenantId, sellerId, LedgerAccountType.WALLET);
        _ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.WALLET, sellerId)
            .Returns(sellerWallet);

        await _sut.RecordDirectChargeFundsAsync(tenantId, sellerId, 100m, 0m, LedgerAccountType.WALLET, "No fee charge");

        // 3 entries: external credit (100), external debit (100), seller credit (100)
        _ledgerRepository.Received(3).AddEntry(Arg.Any<LedgerEntry>());

        externalFunds.Balance.Should().Be(0m);
        sellerWallet.Balance.Should().Be(100m);
    }
}
