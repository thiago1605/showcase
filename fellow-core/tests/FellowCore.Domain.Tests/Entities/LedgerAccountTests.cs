using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Events;

namespace FellowCore.Domain.Tests.Entities;

public class LedgerAccountTests
{
    private static LedgerAccount CreateWallet(decimal initialBalance = 0m)
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), Guid.NewGuid(), LedgerAccountType.WALLET);
        if (initialBalance > 0)
            _ = account.Credit(initialBalance, "Saldo inicial", "SEED", "seed-001");
        account.ClearDomainEvents();
        return account;
    }

    [Fact]
    public void Credit_ShouldIncreaseBalance()
    {
        var account = CreateWallet();

        var result = account.Credit(150m, "Venda PIX", "TRANSACTION", "tx-001");

        result.IsSuccess.Should().BeTrue();
        account.Balance.Should().Be(150m);
    }

    [Fact]
    public void Credit_ShouldCreateLedgerEntry()
    {
        var account = CreateWallet();

        var result = account.Credit(100m, "Venda PIX", "TRANSACTION", "tx-001");

        result.IsSuccess.Should().BeTrue();
        account.Entries.Should().ContainSingle();
        result.Value.Amount.Should().Be(100m);
        result.Value.BalanceAfter.Should().Be(100m);
        result.Value.Description.Should().Be("Venda PIX");
    }

    [Fact]
    public void Credit_ShouldRaiseLedgerFundsRecordedEvent()
    {
        var account = CreateWallet();

        var result = account.Credit(100m, "Venda PIX", "TRANSACTION", "tx-001");

        result.IsSuccess.Should().BeTrue();
        account.DomainEvents.Should().ContainSingle(e => e is LedgerFundsRecordedEvent);
    }

    [Fact]
    public void Credit_ShouldFail_WhenAmountIsZeroOrNegative()
    {
        var account = CreateWallet();

        var result = account.Credit(0m, "Inválido", null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("LedgerAccount.InvalidCreditAmount");
    }

    [Fact]
    public void Debit_ShouldDecreaseBalance()
    {
        var account = CreateWallet(500m);

        var result = account.Debit(200m, "Payout", "PAYOUT", "payout-001");

        result.IsSuccess.Should().BeTrue();
        account.Balance.Should().Be(300m);
    }

    [Fact]
    public void Debit_ShouldFail_WhenInsufficientBalance()
    {
        var account = CreateWallet(100m);

        var result = account.Debit(200m, "Payout", "PAYOUT", "payout-001");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("LedgerAccount.InsufficientBalance");
        account.Balance.Should().Be(100m);
    }

    [Fact]
    public void Debit_ShouldFail_WhenAmountIsZero()
    {
        var account = CreateWallet(100m);

        var result = account.Debit(0m, "Inválido", null, null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("LedgerAccount.InvalidDebitAmount");
    }

    [Fact]
    public void TransferTo_ShouldMoveBalanceBetweenAccounts()
    {
        var source = CreateWallet(500m);
        var target = LedgerAccount.Create(source.TenantId, source.SellerId, LedgerAccountType.WALLET);

        var result = source.TransferTo(target, 300m);

        result.IsSuccess.Should().BeTrue();
        source.Balance.Should().Be(200m);
        target.Balance.Should().Be(300m);
    }

    [Fact]
    public void TransferTo_ShouldFail_WhenInsufficientBalance()
    {
        var source = CreateWallet(100m);
        var target = LedgerAccount.Create(source.TenantId, source.SellerId, LedgerAccountType.WALLET);

        var result = source.TransferTo(target, 200m);

        result.IsFailure.Should().BeTrue();
        source.Balance.Should().Be(100m);
        target.Balance.Should().Be(0m);
    }

    [Fact]
    public void TransferTo_ShouldRaiseFundsTransferredEvent()
    {
        var source = CreateWallet(500m);
        var target = LedgerAccount.Create(source.TenantId, source.SellerId, LedgerAccountType.WALLET);

        source.TransferTo(target, 100m);

        source.DomainEvents.Should().Contain(e => e is FundsTransferredEvent);
    }
}
