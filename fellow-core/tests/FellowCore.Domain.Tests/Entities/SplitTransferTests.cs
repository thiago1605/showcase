using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class SplitTransferTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid TransactionId = Guid.NewGuid();
    private static readonly Guid RecipientSellerId = Guid.NewGuid();

    [Fact]
    public void Create_ShouldSucceed_WithValidData()
    {
        var result = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m, 25m);

        result.IsSuccess.Should().BeTrue();
        result.Value.TransactionId.Should().Be(TransactionId);
        result.Value.TenantId.Should().Be(TenantId);
        result.Value.RecipientSellerId.Should().Be(RecipientSellerId);
        result.Value.Amount.Should().Be(50m);
        result.Value.Percentage.Should().Be(25m);
        result.Value.Status.Should().Be(SplitTransferStatus.PENDING);
        result.Value.FailureReason.Should().BeNull();
        result.Value.ReservedAt.Should().BeNull();
        result.Value.PaidAt.Should().BeNull();
        result.Value.ReversedAt.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldSucceed_WithoutPercentage()
    {
        var result = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 100m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Percentage.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Create_ShouldFail_WhenAmountIsZeroOrNegative(decimal amount)
    {
        var result = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, amount);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitTransfer.InvalidAmount");
    }

    [Fact]
    public void Create_ShouldFail_WhenTransactionIdIsEmpty()
    {
        var result = SplitTransfer.Create(Guid.Empty, TenantId, RecipientSellerId, 50m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitTransfer.InvalidTransactionId");
    }

    [Fact]
    public void Create_ShouldFail_WhenTenantIdIsEmpty()
    {
        var result = SplitTransfer.Create(TransactionId, Guid.Empty, RecipientSellerId, 50m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitTransfer.InvalidTenantId");
    }

    [Fact]
    public void Create_ShouldFail_WhenRecipientSellerIdIsEmpty()
    {
        var result = SplitTransfer.Create(TransactionId, TenantId, Guid.Empty, 50m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitTransfer.InvalidRecipientSellerId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100.01)]
    public void Create_ShouldFail_WhenPercentageIsInvalid(decimal percentage)
    {
        var result = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m, percentage);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitTransfer.InvalidPercentage");
    }

    // --- Status transition tests ---

    [Fact]
    public void Reserve_ShouldSucceed_FromPending()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;

        var result = transfer.Reserve();

        result.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.RESERVED);
        transfer.ReservedAt.Should().NotBeNull();
    }

    [Fact]
    public void Reserve_ShouldFail_FromPaid()
    {
        var transfer = CreatePaidTransfer();

        var result = transfer.Reserve();

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitTransfer.InvalidTransition");
    }

    [Fact]
    public void MarkProcessing_ShouldSucceed_FromReserved()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;
        transfer.Reserve();

        var result = transfer.MarkProcessing();

        result.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.PROCESSING);
    }

    [Fact]
    public void MarkProcessing_ShouldFail_FromPending()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;

        var result = transfer.MarkProcessing();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void MarkPaid_ShouldSucceed_FromProcessing()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;
        transfer.Reserve();
        transfer.MarkProcessing();

        var result = transfer.MarkPaid();

        result.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.PAID);
        transfer.PaidAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkPaid_ShouldFail_FromPending()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;

        var result = transfer.MarkPaid();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Fail_ShouldSucceed_FromPending()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;

        var result = transfer.Fail("Seller account not found");

        result.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.FAILED);
        transfer.FailureReason.Should().Be("Seller account not found");
    }

    [Fact]
    public void Fail_ShouldSucceed_FromReserved()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;
        transfer.Reserve();

        var result = transfer.Fail("Insufficient balance");

        result.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.FAILED);
    }

    [Fact]
    public void Fail_ShouldSucceed_FromProcessing()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;
        transfer.Reserve();
        transfer.MarkProcessing();

        var result = transfer.Fail("Provider timeout");

        result.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.FAILED);
    }

    [Fact]
    public void Fail_ShouldFail_FromReversed()
    {
        var transfer = CreatePaidTransfer();
        transfer.Reverse();

        var result = transfer.Fail("Should not work");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Reverse_ShouldSucceed_FromPaid()
    {
        var transfer = CreatePaidTransfer();

        var result = transfer.Reverse();

        result.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.REVERSED);
        transfer.ReversedAt.Should().NotBeNull();
    }

    [Fact]
    public void Reverse_ShouldSucceed_FromReserved()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;
        transfer.Reserve();

        var result = transfer.Reverse();

        result.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.REVERSED);
    }

    [Fact]
    public void Reverse_ShouldFail_FromPending()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;

        var result = transfer.Reverse();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Reverse_ShouldFail_FromFailed()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;
        transfer.Fail("some reason");

        var result = transfer.Reverse();

        result.IsFailure.Should().BeTrue();
    }

    // --- PartialReverse tests ---

    [Fact]
    public void PartialReverse_ShouldSucceed_FromPaid()
    {
        var transfer = CreatePaidTransfer();

        var result = transfer.PartialReverse(20m);

        result.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.PARTIALLY_REVERSED);
        transfer.ReversedAmount.Should().Be(20m);
        transfer.RemainingAmount.Should().Be(30m);
        transfer.ReversedAt.Should().NotBeNull();
    }

    [Fact]
    public void PartialReverse_ShouldTransitionToReversed_WhenFullAmountReversed()
    {
        var transfer = CreatePaidTransfer();

        transfer.PartialReverse(30m);
        var result = transfer.PartialReverse(20m); // 30 + 20 = 50 = Amount

        result.IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.REVERSED);
        transfer.ReversedAmount.Should().Be(50m);
        transfer.RemainingAmount.Should().Be(0m);
    }

    [Fact]
    public void PartialReverse_ShouldAllowMultiplePartialReversals()
    {
        var transfer = CreatePaidTransfer();

        transfer.PartialReverse(10m).IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.PARTIALLY_REVERSED);

        transfer.PartialReverse(15m).IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.PARTIALLY_REVERSED);
        transfer.ReversedAmount.Should().Be(25m);
        transfer.RemainingAmount.Should().Be(25m);
    }

    [Fact]
    public void PartialReverse_ShouldFail_WhenAmountExceedsRemaining()
    {
        var transfer = CreatePaidTransfer();
        transfer.PartialReverse(40m);

        var result = transfer.PartialReverse(20m); // 40 + 20 = 60 > 50

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitTransfer.ReversalExceedsRemaining");
    }

    [Fact]
    public void PartialReverse_ShouldFail_WhenAmountIsZero()
    {
        var transfer = CreatePaidTransfer();

        var result = transfer.PartialReverse(0m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitTransfer.InvalidReversalAmount");
    }

    [Fact]
    public void PartialReverse_ShouldFail_FromPending()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;

        var result = transfer.PartialReverse(10m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitTransfer.InvalidTransition");
    }

    [Fact]
    public void Reverse_ShouldSetReversedAmountToFull()
    {
        var transfer = CreatePaidTransfer();

        transfer.Reverse();

        transfer.ReversedAmount.Should().Be(50m);
        transfer.RemainingAmount.Should().Be(0m);
    }

    // --- IsValidTransition exhaustive tests ---

    [Theory]
    [InlineData(SplitTransferStatus.PENDING, SplitTransferStatus.RESERVED, true)]
    [InlineData(SplitTransferStatus.PENDING, SplitTransferStatus.FAILED, true)]
    [InlineData(SplitTransferStatus.PENDING, SplitTransferStatus.PROCESSING, false)]
    [InlineData(SplitTransferStatus.PENDING, SplitTransferStatus.PAID, false)]
    [InlineData(SplitTransferStatus.PENDING, SplitTransferStatus.REVERSED, false)]
    [InlineData(SplitTransferStatus.RESERVED, SplitTransferStatus.PROCESSING, true)]
    [InlineData(SplitTransferStatus.RESERVED, SplitTransferStatus.REVERSED, true)]
    [InlineData(SplitTransferStatus.RESERVED, SplitTransferStatus.PARTIALLY_REVERSED, true)]
    [InlineData(SplitTransferStatus.RESERVED, SplitTransferStatus.FAILED, true)]
    [InlineData(SplitTransferStatus.RESERVED, SplitTransferStatus.PAID, false)]
    [InlineData(SplitTransferStatus.PROCESSING, SplitTransferStatus.PAID, true)]
    [InlineData(SplitTransferStatus.PROCESSING, SplitTransferStatus.FAILED, true)]
    [InlineData(SplitTransferStatus.PROCESSING, SplitTransferStatus.REVERSED, false)]
    [InlineData(SplitTransferStatus.PAID, SplitTransferStatus.REVERSED, true)]
    [InlineData(SplitTransferStatus.PAID, SplitTransferStatus.PARTIALLY_REVERSED, true)]
    [InlineData(SplitTransferStatus.PAID, SplitTransferStatus.FAILED, false)]
    [InlineData(SplitTransferStatus.PARTIALLY_REVERSED, SplitTransferStatus.REVERSED, true)]
    [InlineData(SplitTransferStatus.PARTIALLY_REVERSED, SplitTransferStatus.PARTIALLY_REVERSED, true)]
    [InlineData(SplitTransferStatus.PARTIALLY_REVERSED, SplitTransferStatus.PENDING, false)]
    [InlineData(SplitTransferStatus.FAILED, SplitTransferStatus.PENDING, true)]
    [InlineData(SplitTransferStatus.FAILED, SplitTransferStatus.REVERSED, false)]
    [InlineData(SplitTransferStatus.REVERSED, SplitTransferStatus.PENDING, false)]
    public void IsValidTransition_ShouldReturnExpected(SplitTransferStatus from, SplitTransferStatus to, bool expected)
    {
        SplitTransfer.IsValidTransition(from, to).Should().Be(expected);
    }

    // --- Full lifecycle test ---

    [Fact]
    public void FullLifecycle_Pending_Reserve_Processing_Paid_Reverse()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 100m).Value;
        transfer.Status.Should().Be(SplitTransferStatus.PENDING);

        transfer.Reserve().IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.RESERVED);

        transfer.MarkProcessing().IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.PROCESSING);

        transfer.MarkPaid().IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.PAID);

        transfer.Reverse().IsSuccess.Should().BeTrue();
        transfer.Status.Should().Be(SplitTransferStatus.REVERSED);
    }

    private static SplitTransfer CreatePaidTransfer()
    {
        var transfer = SplitTransfer.Create(TransactionId, TenantId, RecipientSellerId, 50m).Value;
        transfer.Reserve();
        transfer.MarkProcessing();
        transfer.MarkPaid();
        return transfer;
    }
}
