using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class RefundIntentTests
{
    [Fact]
    public void Create_ShouldInitializeWithPendingStatus()
    {
        var refund = RefundIntent.Create(Guid.NewGuid(), Guid.NewGuid(), 50m, "duplicate", "refund_key_1");

        refund.Status.Should().Be(RefundIntentStatus.PENDING);
        refund.Amount.Should().Be(50m);
        refund.Reason.Should().Be("duplicate");
        refund.IdempotencyKey.Should().Be("refund_key_1");
        refund.ProviderRefundId.Should().BeNull();
    }

    [Fact]
    public void MarkProcessing_ShouldUpdateStatus()
    {
        var refund = RefundIntent.Create(Guid.NewGuid(), Guid.NewGuid(), 100m);

        refund.MarkProcessing();

        refund.Status.Should().Be(RefundIntentStatus.PROCESSING);
    }

    [Fact]
    public void Complete_ShouldSetProviderRefundId()
    {
        var refund = RefundIntent.Create(Guid.NewGuid(), Guid.NewGuid(), 100m);
        refund.MarkProcessing();

        refund.Complete("re_stripe_123");

        refund.Status.Should().Be(RefundIntentStatus.COMPLETED);
        refund.ProviderRefundId.Should().Be("re_stripe_123");
    }

    [Fact]
    public void Fail_ShouldSetFailedStatus()
    {
        var refund = RefundIntent.Create(Guid.NewGuid(), Guid.NewGuid(), 100m);
        refund.MarkProcessing();

        refund.Fail();

        refund.Status.Should().Be(RefundIntentStatus.FAILED);
    }
}
