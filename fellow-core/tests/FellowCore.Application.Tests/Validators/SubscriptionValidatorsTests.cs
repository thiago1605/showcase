using FluentValidation.TestHelper;
using FellowCore.Application.Modules.Subscriptions.DTOs;
using FellowCore.Application.Modules.Subscriptions.Validators;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Tests.Validators;

public class CreateSubscriptionDtoValidatorTests
{
    private readonly CreateSubscriptionDtoValidator _validator = new();

    private static CreateSubscriptionDto ValidDto() => new(
        SellerId: Guid.NewGuid(),
        Amount: 99.90m,
        Description: "Plano Mensal Premium",
        Interval: BillingInterval.MONTHLY
    );

    [Fact]
    public void Valid_ShouldPass() =>
        _validator.TestValidate(ValidDto()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void SellerId_Empty_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { SellerId = Guid.Empty })
            .ShouldHaveValidationErrorFor(x => x.SellerId);

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void Amount_Invalid_ShouldFail(decimal amount) =>
        _validator.TestValidate(ValidDto() with { Amount = amount })
            .ShouldHaveValidationErrorFor(x => x.Amount);

    [Fact]
    public void Description_Empty_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { Description = "" })
            .ShouldHaveValidationErrorFor(x => x.Description);

    [Fact]
    public void MaxCycles_Zero_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { MaxCycles = 0 })
            .ShouldHaveValidationErrorFor(x => x.MaxCycles);

    [Fact]
    public void MaxCycles_Null_ShouldPass() =>
        _validator.TestValidate(ValidDto())
            .ShouldNotHaveValidationErrorFor(x => x.MaxCycles);

    [Fact]
    public void MaxCycles_Positive_ShouldPass() =>
        _validator.TestValidate(ValidDto() with { MaxCycles = 12 })
            .ShouldNotHaveValidationErrorFor(x => x.MaxCycles);
}
