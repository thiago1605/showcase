using FluentValidation.TestHelper;
using FellowCore.Application.Modules.Payouts.DTOs;
using FellowCore.Application.Modules.Payouts.Validators;

namespace FellowCore.Application.Tests.Validators;

public class CreatePayoutDtoValidatorTests
{
    private readonly CreatePayoutDtoValidator _validator = new();

    [Fact]
    public void Valid_ShouldPass() =>
        _validator.TestValidate(new CreatePayoutDto(Guid.NewGuid(), 1000m))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void SellerId_Empty_ShouldFail() =>
        _validator.TestValidate(new CreatePayoutDto(Guid.Empty, 1000m))
            .ShouldHaveValidationErrorFor(x => x.SellerId);

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Amount_Invalid_ShouldFail(decimal amount) =>
        _validator.TestValidate(new CreatePayoutDto(Guid.NewGuid(), amount))
            .ShouldHaveValidationErrorFor(x => x.Amount);
}
