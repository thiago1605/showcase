using FluentAssertions;
using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Tests.Entities;

public class PricingPlanTests
{
    [Fact]
    public void Create_ShouldSucceed_WithValidData()
    {
        var result = PricingPlan.Create(
            code: "COMECE",
            name: "Plano Comece",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("COMECE");
        result.Value.Name.Should().Be("Plano Comece");
        result.Value.MonthlyFee.Should().Be(0m);
        result.Value.PixPercentFee.Should().Be(1.50m);
        result.Value.PixMinFee.Should().Be(0.50m);
        result.Value.PixMaxFee.Should().BeNull();
        result.Value.IsActive.Should().BeTrue();
        result.Value.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_ShouldSucceed_WithPixMaxFee()
    {
        var result = PricingPlan.Create(
            code: "CRESCA",
            name: "Plano Cresca",
            monthlyFee: 49.90m,
            pixPercentFee: 0.99m,
            pixMinFee: 0.50m,
            pixMaxFee: 7.90m,
            debitPercentFee: 1.90m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.49m,
            creditCashFixedFee: 0.39m,
            creditInstallmentPercentFee: 5.99m,
            creditInstallmentFixedFee: 0.39m,
            boletoFixedFee: 2.99m,
            payoutFixedFee: 2.50m,
            includedPayoutsPerMonth: 5,
            extraPayoutFixedFee: 2.50m);

        result.IsSuccess.Should().BeTrue();
        result.Value.PixMaxFee.Should().Be(7.90m);
        result.Value.IncludedPayoutsPerMonth.Should().Be(5);
    }

    [Fact]
    public void Create_ShouldFail_WhenCodeIsEmpty()
    {
        var result = PricingPlan.Create(
            code: "",
            name: "Plano Test",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PricingPlan.InvalidCode");
    }

    [Fact]
    public void Create_ShouldFail_WhenNameIsEmpty()
    {
        var result = PricingPlan.Create(
            code: "TEST",
            name: "",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PricingPlan.InvalidName");
    }

    [Fact]
    public void Create_ShouldFail_WhenMonthlyFeeIsNegative()
    {
        var result = PricingPlan.Create(
            code: "TEST",
            name: "Test Plan",
            monthlyFee: -10m,
            pixPercentFee: 1.50m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PricingPlan.InvalidMonthlyFee");
    }

    [Fact]
    public void Create_ShouldFail_WhenPixPercentFeeIsNegative()
    {
        var result = PricingPlan.Create(
            code: "TEST",
            name: "Test Plan",
            monthlyFee: 0m,
            pixPercentFee: -1m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PricingPlan.InvalidPixPercentFee");
    }

    [Fact]
    public void Create_ShouldFail_WhenPixPercentFeeExceeds100()
    {
        var result = PricingPlan.Create(
            code: "TEST",
            name: "Test Plan",
            monthlyFee: 0m,
            pixPercentFee: 101m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PricingPlan.InvalidPixPercentFee");
    }

    [Fact]
    public void Create_ShouldFail_WhenPixMinFeeIsNegative()
    {
        var result = PricingPlan.Create(
            code: "TEST",
            name: "Test Plan",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: -1m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PricingPlan.InvalidPixMinFee");
    }

    [Fact]
    public void Create_ShouldFail_WhenPixMaxFeeLessThanMin()
    {
        var result = PricingPlan.Create(
            code: "TEST",
            name: "Test Plan",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: 5.00m,
            pixMaxFee: 2.00m,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PricingPlan.PixMaxFeeLessThanMin");
    }

    [Fact]
    public void Create_ShouldFail_WhenCreditCashPercentFeeIsNegative()
    {
        var result = PricingPlan.Create(
            code: "TEST",
            name: "Test Plan",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: -1m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PricingPlan.InvalidCreditCashPercentFee");
    }

    [Fact]
    public void Create_ShouldFail_WhenBoletoFixedFeeIsNegative()
    {
        var result = PricingPlan.Create(
            code: "TEST",
            name: "Test Plan",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: -1m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PricingPlan.InvalidBoletoFixedFee");
    }

    [Fact]
    public void Create_ShouldFail_WhenIncludedPayoutsIsNegative()
    {
        var result = PricingPlan.Create(
            code: "TEST",
            name: "Test Plan",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: -1,
            extraPayoutFixedFee: 3.67m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PricingPlan.InvalidIncludedPayouts");
    }

    [Fact]
    public void Create_ShouldNormalizeCode_ToUpperCase()
    {
        var result = PricingPlan.Create(
            code: "comece",
            name: "Plano Comece",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("COMECE");
    }

    [Fact]
    public void Deactivate_ShouldSetIsActiveFalse()
    {
        var plan = PricingPlan.Create(
            code: "TEST",
            name: "Test Plan",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m).Value;

        plan.Deactivate();

        plan.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_ShouldSetIsActiveTrue()
    {
        var plan = PricingPlan.Create(
            code: "TEST",
            name: "Test Plan",
            monthlyFee: 0m,
            pixPercentFee: 1.50m,
            pixMinFee: 0.50m,
            pixMaxFee: null,
            debitPercentFee: 2.00m,
            debitFixedFee: 0m,
            creditCashPercentFee: 4.99m,
            creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6.49m,
            creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3.49m,
            payoutFixedFee: 3.67m,
            includedPayoutsPerMonth: 0,
            extraPayoutFixedFee: 3.67m).Value;

        plan.Deactivate();
        plan.Activate();

        plan.IsActive.Should().BeTrue();
    }
}
