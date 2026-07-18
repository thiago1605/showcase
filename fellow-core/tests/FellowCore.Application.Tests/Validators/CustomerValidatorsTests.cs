using FluentValidation.TestHelper;
using FellowCore.Application.Modules.Customers.DTOs;
using FellowCore.Application.Modules.Customers.Validators;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Tests.Validators;

public class CreateCustomerDtoValidatorTests
{
    private readonly CreateCustomerDtoValidator _validator = new();

    [Fact]
    public void Valid_ShouldPass() =>
        _validator.TestValidate(new CreateCustomerDto("John", "j@test.com"))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Name_Empty_ShouldFail() =>
        _validator.TestValidate(new CreateCustomerDto("", "j@test.com"))
            .ShouldHaveValidationErrorFor(x => x.Name);

    [Fact]
    public void Email_Invalid_ShouldFail() =>
        _validator.TestValidate(new CreateCustomerDto("John", "invalid"))
            .ShouldHaveValidationErrorFor(x => x.Email);

    [Fact]
    public void Document_InvalidFormat_ShouldFail() =>
        _validator.TestValidate(new CreateCustomerDto("John", "j@t.com", Document: "abc"))
            .ShouldHaveValidationErrorFor(x => x.Document);

    [Fact]
    public void Document_Null_ShouldPass() =>
        _validator.TestValidate(new CreateCustomerDto("John", "j@t.com"))
            .ShouldNotHaveValidationErrorFor(x => x.Document);
}

public class AddPaymentMethodDtoValidatorTests
{
    private readonly AddPaymentMethodDtoValidator _validator = new();

    private static AddPaymentMethodDto ValidDto() => new(
        Type: PaymentType.CREDIT_CARD,
        Token: "tok_123",
        Gateway: PaymentProvider.STRIPE,
        First6: "411111",
        Last4: "1111",
        Brand: "Visa",
        Expiration: "12/30",
        HolderName: "John Doe"
    );

    [Fact]
    public void Valid_ShouldPass() =>
        _validator.TestValidate(ValidDto()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Token_Empty_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { Token = "" })
            .ShouldHaveValidationErrorFor(x => x.Token);

    [Fact]
    public void First6_Invalid_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { First6 = "123" })
            .ShouldHaveValidationErrorFor(x => x.First6);

    [Fact]
    public void Last4_Invalid_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { Last4 = "12" })
            .ShouldHaveValidationErrorFor(x => x.Last4);
}
