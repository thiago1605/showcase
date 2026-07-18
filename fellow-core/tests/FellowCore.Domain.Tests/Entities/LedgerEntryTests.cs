using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class LedgerEntryTests
{
    private static LedgerAccount CreateWalletWithCredit(decimal amount = 100m)
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), Guid.NewGuid(), LedgerAccountType.WALLET);
        account.Credit(amount, "Seed", "SEED", "seed-001");
        account.ClearDomainEvents();
        return account;
    }

    [Fact]
    public void Create_ShouldSetAllProperties()
    {
        var accountId = Guid.NewGuid();
        var fixedTime = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

        var entry = LedgerEntry.Create(accountId, 150m, 150m, "Venda PIX", "TRANSACTION", "tx-001", fixedTime);

        entry.Id.Should().NotBeEmpty();
        entry.AccountId.Should().Be(accountId);
        entry.Amount.Should().Be(150m);
        entry.BalanceAfter.Should().Be(150m);
        entry.Description.Should().Be("Venda PIX");
        entry.ReferenceType.Should().Be("TRANSACTION");
        entry.ReferenceId.Should().Be("tx-001");
        entry.AvailableAt.Should().Be(fixedTime);
        entry.CreatedAt.Should().Be(fixedTime);
        entry.ContraEntryId.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldUseUtcNow_WhenNoTimestampProvided()
    {
        var entry = LedgerEntry.Create(Guid.NewGuid(), 50m, 50m, "Test", null, null);

        entry.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        entry.AvailableAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Create_ShouldAllowNegativeAmount_ForDebits()
    {
        var entry = LedgerEntry.Create(Guid.NewGuid(), -75m, 25m, "Payout", "PAYOUT", "payout-001");

        entry.Amount.Should().Be(-75m);
        entry.BalanceAfter.Should().Be(25m);
    }

    [Fact]
    public void Create_ShouldAllowNullReferenceFields()
    {
        var entry = LedgerEntry.Create(Guid.NewGuid(), 10m, 10m, "Manual adjustment", null, null);

        entry.ReferenceType.Should().BeNull();
        entry.ReferenceId.Should().BeNull();
    }

    [Fact]
    public void LinkContraEntry_ShouldSetContraEntryId()
    {
        var entry = LedgerEntry.Create(Guid.NewGuid(), 100m, 100m, "Credit", "TX", "tx-001");
        var contraId = Guid.NewGuid();

        entry.LinkContraEntry(contraId);

        entry.ContraEntryId.Should().Be(contraId);
    }

    [Fact]
    public void LinkContraEntry_ShouldOverwritePreviousContraEntry()
    {
        var entry = LedgerEntry.Create(Guid.NewGuid(), 100m, 100m, "Credit", "TX", "tx-001");
        var firstContraId = Guid.NewGuid();
        var secondContraId = Guid.NewGuid();

        entry.LinkContraEntry(firstContraId);
        entry.LinkContraEntry(secondContraId);

        entry.ContraEntryId.Should().Be(secondContraId);
    }

    [Fact]
    public void CreditOnAccount_ShouldProduceLedgerEntryWithPositiveAmount()
    {
        var account = LedgerAccount.Create(Guid.NewGuid(), Guid.NewGuid(), LedgerAccountType.WALLET);

        var result = account.Credit(200m, "Venda", "TRANSACTION", "tx-100");

        result.IsSuccess.Should().BeTrue();
        result.Value.Amount.Should().Be(200m);
        result.Value.BalanceAfter.Should().Be(200m);
    }

    [Fact]
    public void DebitOnAccount_ShouldProduceLedgerEntryWithNegativeAmount()
    {
        var account = CreateWalletWithCredit(500m);

        var result = account.Debit(150m, "Payout", "PAYOUT", "payout-100");

        result.IsSuccess.Should().BeTrue();
        result.Value.Amount.Should().Be(-150m);
        result.Value.BalanceAfter.Should().Be(350m);
    }

    [Fact]
    public void ContraEntries_ShouldBeLinkedBidirectionally()
    {
        var debitEntry = LedgerEntry.Create(Guid.NewGuid(), -100m, 0m, "Debit", "TRANSFER", "t-001");
        var creditEntry = LedgerEntry.Create(Guid.NewGuid(), 100m, 100m, "Credit", "TRANSFER", "t-001");

        debitEntry.LinkContraEntry(creditEntry.Id);
        creditEntry.LinkContraEntry(debitEntry.Id);

        debitEntry.ContraEntryId.Should().Be(creditEntry.Id);
        creditEntry.ContraEntryId.Should().Be(debitEntry.Id);
    }
}
