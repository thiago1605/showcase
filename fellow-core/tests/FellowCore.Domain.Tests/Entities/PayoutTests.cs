using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class PayoutTests
{
    private static Payout CreateValidPayout(decimal amount = 500m, decimal fee = 2m) =>
        Payout.Create(Guid.NewGuid(), Guid.NewGuid(), amount, fee).Value;

    [Fact]
    public void Create_ShouldSucceed_WithValidData()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var result = Payout.Create(tenantId, sellerId, 1000m, 5m);

        result.IsSuccess.Should().BeTrue();
        result.Value.TenantId.Should().Be(tenantId);
        result.Value.SellerId.Should().Be(sellerId);
        result.Value.Amount.Should().Be(1000m);
        result.Value.Fee.Should().Be(5m);
        result.Value.Status.Should().Be(PayoutStatus.PENDING);
        result.Value.BankTransactionId.Should().BeNull();
        result.Value.FailureReason.Should().BeNull();
        result.Value.ProcessedAt.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldDefaultFeeToZero()
    {
        var result = Payout.Create(Guid.NewGuid(), Guid.NewGuid(), 100m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Fee.Should().Be(0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Create_ShouldFail_WhenAmountIsZeroOrNegative(decimal amount)
    {
        var result = Payout.Create(Guid.NewGuid(), Guid.NewGuid(), amount);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Payout.InvalidAmount");
    }

    [Fact]
    public void Create_ShouldFail_WhenFeeIsNegative()
    {
        var result = Payout.Create(Guid.NewGuid(), Guid.NewGuid(), 100m, -1m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Payout.InvalidFee");
    }

    [Fact]
    public void MarkAsProcessing_ShouldChangeStatusToProcessing()
    {
        var payout = CreateValidPayout();

        payout.MarkAsProcessing();

        payout.Status.Should().Be(PayoutStatus.PROCESSING);
        payout.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Complete_ShouldSetPaidStatusAndBankTransactionId()
    {
        var payout = CreateValidPayout();
        payout.MarkAsProcessing();

        payout.Complete("bank_tx_123");

        payout.Status.Should().Be(PayoutStatus.PAID);
        payout.BankTransactionId.Should().Be("bank_tx_123");
        payout.ProcessedAt.Should().NotBeNull();
        payout.ProcessedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Fail_ShouldSetFailedStatusAndReason()
    {
        var payout = CreateValidPayout();
        payout.MarkAsProcessing();

        payout.Fail("Saldo insuficiente");

        payout.Status.Should().Be(PayoutStatus.FAILED);
        payout.FailureReason.Should().Be("Saldo insuficiente");
    }

    [Fact]
    public void Fail_ShouldAllowNullReason()
    {
        var payout = CreateValidPayout();

        payout.Fail();

        payout.Status.Should().Be(PayoutStatus.FAILED);
        payout.FailureReason.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldUseProvidedTimestamp()
    {
        var fixedTime = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        var result = Payout.Create(Guid.NewGuid(), Guid.NewGuid(), 200m, 0m, fixedTime);

        result.Value.CreatedAt.Should().Be(fixedTime);
        result.Value.UpdatedAt.Should().Be(fixedTime);
    }

    [Fact]
    public void MarkAsProcessing_ShouldUseProvidedTimestamp()
    {
        var payout = CreateValidPayout();
        var fixedTime = new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

        payout.MarkAsProcessing(fixedTime);

        payout.UpdatedAt.Should().Be(fixedTime);
    }

    [Fact]
    public void Complete_ShouldUseProvidedTimestamp()
    {
        var payout = CreateValidPayout();
        var fixedTime = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

        payout.Complete("bank_tx_456", fixedTime);

        payout.ProcessedAt.Should().Be(fixedTime);
        payout.UpdatedAt.Should().Be(fixedTime);
    }

    [Fact]
    public void Fail_ShouldUseProvidedTimestamp()
    {
        var payout = CreateValidPayout();
        var fixedTime = new DateTime(2026, 4, 1, 14, 30, 0, DateTimeKind.Utc);

        payout.Fail("timeout", fixedTime);

        payout.UpdatedAt.Should().Be(fixedTime);
    }

    [Fact]
    public void FullLifecycle_Pending_Processing_Paid()
    {
        var payout = CreateValidPayout(1000m, 10m);

        payout.Status.Should().Be(PayoutStatus.PENDING);

        payout.MarkAsProcessing();
        payout.Status.Should().Be(PayoutStatus.PROCESSING);

        payout.Complete("bank_tx_final");
        payout.Status.Should().Be(PayoutStatus.PAID);
        payout.BankTransactionId.Should().Be("bank_tx_final");
        payout.ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public void FullLifecycle_Pending_Processing_Failed()
    {
        var payout = CreateValidPayout();

        payout.MarkAsProcessing();
        payout.Fail("Provider error");

        payout.Status.Should().Be(PayoutStatus.FAILED);
        payout.FailureReason.Should().Be("Provider error");
        payout.ProcessedAt.Should().BeNull();
    }
}
