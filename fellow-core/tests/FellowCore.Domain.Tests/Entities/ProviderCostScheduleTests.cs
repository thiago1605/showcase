using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class ProviderCostScheduleTests
{
    [Fact]
    public void Create_ShouldSucceed_WithValidData()
    {
        var result = ProviderCostSchedule.Create(
            provider: PaymentProvider.STRIPE,
            paymentType: PaymentType.CREDIT_CARD,
            percentFee: 3.99m,
            fixedFee: 0.39m,
            minFee: 0m,
            maxFee: null,
            description: "Stripe national card");

        result.IsSuccess.Should().BeTrue();
        result.Value.Provider.Should().Be(PaymentProvider.STRIPE);
        result.Value.PaymentType.Should().Be(PaymentType.CREDIT_CARD);
        result.Value.PercentFee.Should().Be(3.99m);
        result.Value.FixedFee.Should().Be(0.39m);
        result.Value.MinFee.Should().Be(0m);
        result.Value.MaxFee.Should().BeNull();
        result.Value.Description.Should().Be("Stripe national card");
        result.Value.IsActive.Should().BeTrue();
        result.Value.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_ShouldSucceed_WithMinAndMaxFee()
    {
        var result = ProviderCostSchedule.Create(
            provider: PaymentProvider.OPENPIX,
            paymentType: PaymentType.PIX,
            percentFee: 0.80m,
            fixedFee: 0m,
            minFee: 0.50m,
            maxFee: 5.00m,
            description: "OpenPix PIX");

        result.IsSuccess.Should().BeTrue();
        result.Value.MinFee.Should().Be(0.50m);
        result.Value.MaxFee.Should().Be(5.00m);
    }

    [Fact]
    public void Create_ShouldFail_WhenPercentFeeIsNegative()
    {
        var result = ProviderCostSchedule.Create(
            provider: PaymentProvider.STRIPE,
            paymentType: PaymentType.CREDIT_CARD,
            percentFee: -1m,
            fixedFee: 0.39m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderCostSchedule.NegativePercentFee");
    }

    [Fact]
    public void Create_ShouldFail_WhenFixedFeeIsNegative()
    {
        var result = ProviderCostSchedule.Create(
            provider: PaymentProvider.STRIPE,
            paymentType: PaymentType.CREDIT_CARD,
            percentFee: 3.99m,
            fixedFee: -0.39m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderCostSchedule.NegativeFixedFee");
    }

    [Fact]
    public void Create_ShouldFail_WhenMinFeeIsNegative()
    {
        var result = ProviderCostSchedule.Create(
            provider: PaymentProvider.OPENPIX,
            paymentType: PaymentType.PIX,
            percentFee: 0.80m,
            fixedFee: 0m,
            minFee: -1m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderCostSchedule.NegativeMinFee");
    }

    [Fact]
    public void Create_ShouldFail_WhenMaxFeeIsNegative()
    {
        var result = ProviderCostSchedule.Create(
            provider: PaymentProvider.OPENPIX,
            paymentType: PaymentType.PIX,
            percentFee: 0.80m,
            fixedFee: 0m,
            minFee: 0m,
            maxFee: -1m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderCostSchedule.NegativeMaxFee");
    }

    [Fact]
    public void Create_ShouldFail_WhenMaxFeeLessThanMinFee()
    {
        var result = ProviderCostSchedule.Create(
            provider: PaymentProvider.OPENPIX,
            paymentType: PaymentType.PIX,
            percentFee: 0.80m,
            fixedFee: 0m,
            minFee: 5.00m,
            maxFee: 2.00m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProviderCostSchedule.MaxFeeLessThanMinFee");
    }

    [Fact]
    public void Deactivate_ShouldSetIsActiveFalse()
    {
        var schedule = ProviderCostSchedule.Create(
            provider: PaymentProvider.STRIPE,
            paymentType: PaymentType.CREDIT_CARD,
            percentFee: 3.99m,
            fixedFee: 0.39m).Value;

        schedule.Deactivate();

        schedule.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_ShouldSetIsActiveTrue()
    {
        var schedule = ProviderCostSchedule.Create(
            provider: PaymentProvider.STRIPE,
            paymentType: PaymentType.CREDIT_CARD,
            percentFee: 3.99m,
            fixedFee: 0.39m).Value;

        schedule.Deactivate();
        schedule.Activate();

        schedule.IsActive.Should().BeTrue();
    }
}
