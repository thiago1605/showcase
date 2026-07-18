using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class SubscriptionTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    [Fact]
    public void Create_WithValidData_ShouldReturnSuccess()
    {
        var result = Subscription.Create(TenantId, SellerId, 99.90m, "Plano Mensal", BillingInterval.MONTHLY);

        result.IsSuccess.Should().BeTrue();
        var sub = result.Value;
        sub.TenantId.Should().Be(TenantId);
        sub.SellerId.Should().Be(SellerId);
        sub.Amount.Should().Be(99.90m);
        sub.Description.Should().Be("Plano Mensal");
        sub.Interval.Should().Be(BillingInterval.MONTHLY);
        sub.Status.Should().Be(SubscriptionStatus.ACTIVE);
        sub.CycleCount.Should().Be(0);
        sub.DomainEvents.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Create_WithInvalidAmount_ShouldFail(decimal amount)
    {
        var result = Subscription.Create(TenantId, SellerId, amount, "Plano", BillingInterval.MONTHLY);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Subscription.InvalidAmount");
    }

    [Fact]
    public void Create_WithEmptyDescription_ShouldFail()
    {
        var result = Subscription.Create(TenantId, SellerId, 100m, "", BillingInterval.MONTHLY);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Subscription.DescriptionRequired");
    }

    [Fact]
    public void Create_WithCustomStartDate_ShouldUseIt()
    {
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = Subscription.Create(TenantId, SellerId, 50m, "Plano", BillingInterval.WEEKLY, startDate: start);

        result.Value.StartDate.Should().Be(start);
        result.Value.NextBillingDate.Should().Be(start);
    }

    [Fact]
    public void Create_WithMaxCycles_ShouldSetIt()
    {
        var result = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY, maxCycles: 12);

        result.Value.MaxCycles.Should().Be(12);
    }

    [Fact]
    public void AdvanceCycle_Monthly_ShouldIncrementAndAdvanceDate()
    {
        var start = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY, startDate: start).Value;

        sub.AdvanceCycle();

        sub.CycleCount.Should().Be(1);
        sub.NextBillingDate.Should().Be(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void AdvanceCycle_Weekly_ShouldAdvanceBy7Days()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sub = Subscription.Create(TenantId, SellerId, 50m, "Semanal", BillingInterval.WEEKLY, startDate: start).Value;

        sub.AdvanceCycle();

        sub.NextBillingDate.Should().Be(new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void AdvanceCycle_Quarterly_ShouldAdvanceBy3Months()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sub = Subscription.Create(TenantId, SellerId, 200m, "Trimestral", BillingInterval.QUARTERLY, startDate: start).Value;

        sub.AdvanceCycle();

        sub.NextBillingDate.Should().Be(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void AdvanceCycle_Yearly_ShouldAdvanceBy1Year()
    {
        var start = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        var sub = Subscription.Create(TenantId, SellerId, 1200m, "Anual", BillingInterval.YEARLY, startDate: start).Value;

        sub.AdvanceCycle();

        sub.NextBillingDate.Should().Be(new DateTime(2027, 3, 10, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void AdvanceCycle_WhenMaxCyclesReached_ShouldExpire()
    {
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY, maxCycles: 2).Value;

        sub.AdvanceCycle(); // cycle 1
        sub.Status.Should().Be(SubscriptionStatus.ACTIVE);

        sub.AdvanceCycle(); // cycle 2 = max
        sub.Status.Should().Be(SubscriptionStatus.EXPIRED);
        sub.EndDate.Should().NotBeNull();
    }

    [Fact]
    public void Cancel_ShouldSetStatusAndEndDate()
    {
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY).Value;

        sub.Cancel();

        sub.Status.Should().Be(SubscriptionStatus.CANCELED);
        sub.EndDate.Should().NotBeNull();
    }

    [Fact]
    public void Cancel_WhenAlreadyCanceled_ShouldBeIdempotent()
    {
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY).Value;
        sub.Cancel();
        var endDate = sub.EndDate;

        sub.Cancel(); // second call

        sub.EndDate.Should().Be(endDate);
    }

    [Fact]
    public void Pause_ShouldSetStatusPaused()
    {
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY).Value;

        sub.Pause();

        sub.Status.Should().Be(SubscriptionStatus.PAUSED);
    }

    [Fact]
    public void Pause_WhenNotActive_ShouldDoNothing()
    {
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY).Value;
        sub.Cancel();

        sub.Pause();

        sub.Status.Should().Be(SubscriptionStatus.CANCELED);
    }

    [Fact]
    public void Resume_ShouldSetStatusActive()
    {
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY).Value;
        sub.Pause();

        sub.Resume();

        sub.Status.Should().Be(SubscriptionStatus.ACTIVE);
    }

    [Fact]
    public void Resume_WhenNotPaused_ShouldDoNothing()
    {
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY).Value;

        sub.Resume(); // already active

        sub.Status.Should().Be(SubscriptionStatus.ACTIVE);
    }

    [Fact]
    public void IsDueForBilling_WhenActiveAndDatePassed_ShouldReturnTrue()
    {
        var past = DateTime.UtcNow.AddDays(-1);
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY, startDate: past).Value;

        sub.IsDueForBilling(DateTime.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void IsDueForBilling_WhenPaused_ShouldReturnFalse()
    {
        var past = DateTime.UtcNow.AddDays(-1);
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY, startDate: past).Value;
        sub.Pause();

        sub.IsDueForBilling(DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void IsDueForBilling_WhenFutureDate_ShouldReturnFalse()
    {
        var future = DateTime.UtcNow.AddDays(10);
        var sub = Subscription.Create(TenantId, SellerId, 100m, "Plano", BillingInterval.MONTHLY, startDate: future).Value;

        sub.IsDueForBilling(DateTime.UtcNow).Should().BeFalse();
    }
}
