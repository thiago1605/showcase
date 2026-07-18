using FluentAssertions;
using FluentValidation.TestHelper;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Validators;
using FellowCore.Domain.Enums;
using Microsoft.Extensions.Configuration;

namespace FellowCore.Application.Tests.Validators;

public class CreateTransactionDtoValidatorTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static IConfiguration ConfigWithPixCap(decimal cap) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenPix:MaxPixPerTxBrl"] = cap.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .Build();

    private readonly CreateTransactionDtoValidator _validator = new(EmptyConfig());

    private static CreateTransactionDto ValidDto() => new(
        SellerId: Guid.NewGuid(),
        Amount: 100m,
        PaymentType: PaymentType.PIX,
        Installments: 1,
        Description: "Pagamento teste",
        Payer: new PayerDto("Maria Silva", "12345678901", "maria@email.com")
    );

    [Fact]
    public void Valid_ShouldPass() =>
        _validator.TestValidate(ValidDto()).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Amount_Invalid_ShouldFail(decimal amount) =>
        _validator.TestValidate(ValidDto() with { Amount = amount })
            .ShouldHaveValidationErrorFor(x => x.Amount);

    [Fact]
    public void Installments_Zero_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { Installments = 0 })
            .ShouldHaveValidationErrorFor(x => x.Installments);

    [Fact]
    public void Description_Empty_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { Description = "" })
            .ShouldHaveValidationErrorFor(x => x.Description);

    [Fact]
    public void Payer_Null_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { Payer = null! })
            .ShouldHaveValidationErrorFor(x => x.Payer);

    // --- PIX cap (Woovi R$ 800/tx) ---
    [Fact]
    public void Pix_AtDefaultCap_ShouldPass() =>
        _validator.TestValidate(ValidDto() with { Amount = 800m, PaymentType = PaymentType.PIX })
            .ShouldNotHaveValidationErrorFor(x => x.Amount);

    [Fact]
    public void Pix_AboveDefaultCap_ShouldFail()
    {
        var result = _validator.TestValidate(ValidDto() with { Amount = 800.01m, PaymentType = PaymentType.PIX });
        result.ShouldHaveValidationErrorFor(x => x.Amount)
            .WithErrorMessage("O valor máximo por PIX é R$ 800,00 (limite do gateway). Para valores maiores, use boleto, cartão de crédito, ou solicite elevação de limite à Fellow Pay.");
    }

    [Fact]
    public void NonPix_AboveDefaultPixCap_ShouldPass() =>
        _validator.TestValidate(ValidDto() with { Amount = 1500m, PaymentType = PaymentType.CREDIT_CARD })
            .ShouldNotHaveValidationErrorFor(x => x.Amount);

    [Fact]
    public void Pix_AboveConfiguredHigherCap_ShouldPass()
    {
        var v = new CreateTransactionDtoValidator(ConfigWithPixCap(5000m));
        v.TestValidate(ValidDto() with { Amount = 3000m, PaymentType = PaymentType.PIX })
            .ShouldNotHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void Pix_AboveConfiguredLowerCap_ShouldFail()
    {
        var v = new CreateTransactionDtoValidator(ConfigWithPixCap(500m));
        v.TestValidate(ValidDto() with { Amount = 600m, PaymentType = PaymentType.PIX })
            .ShouldHaveValidationErrorFor(x => x.Amount);
    }
}

public class PayerDtoValidatorTests
{
    private readonly PayerDtoValidator _validator = new();

    [Fact]
    public void Valid_ShouldPass() =>
        _validator.TestValidate(new PayerDto("Nome", "12345678901", "e@e.com"))
            .ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Name_Empty_ShouldFail() =>
        _validator.TestValidate(new PayerDto("", "12345678901", "e@e.com"))
            .ShouldHaveValidationErrorFor(x => x.Name);

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("123456789012345")]
    public void Document_Invalid_ShouldFail(string doc) =>
        _validator.TestValidate(new PayerDto("Nome", doc, "e@e.com"))
            .ShouldHaveValidationErrorFor(x => x.Document);

    [Fact]
    public void Email_Invalid_ShouldFail() =>
        _validator.TestValidate(new PayerDto("Nome", "12345678901", "invalido"))
            .ShouldHaveValidationErrorFor(x => x.Email);
}

public class RefundRequestDtoValidatorTests
{
    private readonly RefundRequestDtoValidator _validator = new();

    [Fact]
    public void Empty_ShouldPass() =>
        _validator.TestValidate(new RefundRequestDto()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Amount_Zero_ShouldFail() =>
        _validator.TestValidate(new RefundRequestDto(Amount: 0))
            .ShouldHaveValidationErrorFor(x => x.Amount);

    [Fact]
    public void Amount_Positive_ShouldPass() =>
        _validator.TestValidate(new RefundRequestDto(Amount: 10m))
            .ShouldNotHaveValidationErrorFor(x => x.Amount);
}
