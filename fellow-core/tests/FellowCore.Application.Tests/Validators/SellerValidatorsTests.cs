using FluentValidation.TestHelper;
using FellowCore.Application.Modules.Sellers.DTOs;
using FellowCore.Application.Modules.Sellers.Validators;

namespace FellowCore.Application.Tests.Validators;

public class CreateSellerDtoValidatorTests
{
    private readonly CreateSellerDtoValidator _validator = new();

    private static CreateSellerDto ValidDto() => new(
        LegalName: "Empresa LTDA",
        TradeName: "Empresa",
        Document: "12345678000199",
        Email: "empresa@test.com",
        IncomeValue: 10000m,
        BirthDate: "1990-01-15",
        MobilePhone: "11999999999",
        Address: ValidAddress(),
        FeeDebit: null, FeeCreditCash: null, FeeCreditInstallment: null, FeePixIn: null, PayoutFixedFee: null, PayoutPercentFee: null,
        BusinessDescription: null, BusinessProduct: null, BusinessLifetime: null, BusinessGoal: null, Documents: null
    );

    private static SellerAddressDto ValidAddress() => new(
        "Rua Teste", "123", null, "Centro", "Salvador", "BA", "40000000"
    );

    [Fact]
    public void Valid_ShouldPass() =>
        _validator.TestValidate(ValidDto()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void LegalName_Empty_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { LegalName = "" })
            .ShouldHaveValidationErrorFor(x => x.LegalName);

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("1234567890123456")]
    public void Document_Invalid_ShouldFail(string doc) =>
        _validator.TestValidate(ValidDto() with { Document = doc })
            .ShouldHaveValidationErrorFor(x => x.Document);

    [Fact]
    public void Email_Invalid_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { Email = "not-email" })
            .ShouldHaveValidationErrorFor(x => x.Email);

    [Fact]
    public void BirthDate_InvalidFormat_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { BirthDate = "15/01/1990" })
            .ShouldHaveValidationErrorFor(x => x.BirthDate);

    [Fact]
    public void MobilePhone_Invalid_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { MobilePhone = "123" })
            .ShouldHaveValidationErrorFor(x => x.MobilePhone);

    [Fact]
    public void Address_Null_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { Address = null! })
            .ShouldHaveValidationErrorFor(x => x.Address);

    [Fact]
    public void FeeDebit_Above100_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { FeeDebit = 101m })
            .ShouldHaveValidationErrorFor(x => x.FeeDebit);

    [Fact]
    public void PayoutFixedFee_Negative_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { PayoutFixedFee = -1m })
            .ShouldHaveValidationErrorFor(x => x.PayoutFixedFee);
}

public class SellerAddressDtoValidatorTests
{
    private readonly SellerAddressDtoValidator _validator = new();

    [Fact]
    public void Valid_ShouldPass() =>
        _validator.TestValidate(new SellerAddressDto("Rua", "1", null, "Bairro", "Cidade", "BA", "40000000"))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void ZipCode_Invalid_ShouldFail() =>
        _validator.TestValidate(new SellerAddressDto("Rua", "1", null, "Bairro", "Cidade", "BA", "4000"))
            .ShouldHaveValidationErrorFor(x => x.ZipCode);

    [Fact]
    public void State_Invalid_ShouldFail() =>
        _validator.TestValidate(new SellerAddressDto("Rua", "1", null, "Bairro", "Cidade", "BAH", "40000000"))
            .ShouldHaveValidationErrorFor(x => x.State);
}
