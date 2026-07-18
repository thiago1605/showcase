using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class DisputeTests
{
    [Fact]
    public void Create_ShouldInitializeWithOpenStatus()
    {
        var dispute = Dispute.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "dp_001", 100m, "fraudulent");

        dispute.Status.Should().Be(DisputeStatus.OPEN);
        dispute.ExternalDisputeId.Should().Be("dp_001");
        dispute.Amount.Should().Be(100m);
        dispute.Reason.Should().Be("fraudulent");
        dispute.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public void Win_ShouldSetStatusAndResolvedAt()
    {
        var dispute = Dispute.Create(Guid.NewGuid(), Guid.NewGuid(), null, "dp_002", 50m);

        dispute.Win();

        dispute.Status.Should().Be(DisputeStatus.WON);
        dispute.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public void Lose_ShouldSetStatusAndResolvedAt()
    {
        var dispute = Dispute.Create(Guid.NewGuid(), Guid.NewGuid(), null, "dp_003", 200m);

        dispute.Lose();

        dispute.Status.Should().Be(DisputeStatus.LOST);
        dispute.ResolvedAt.Should().NotBeNull();
    }
}
