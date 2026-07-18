using FluentValidation.TestHelper;
using FellowCore.Application.Modules.Ledgers.DTOs;
using FellowCore.Application.Modules.Ledgers.Validators;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Tests.Validators;

public class CreateLedgerCreditDtoValidatorTests
{
    private readonly CreateLedgerCreditDtoValidator _validator = new();

    private static CreateLedgerCreditDto ValidDto() => new(
        Amount: 100m,
        Description: "Crédito teste",
        TransactionId: "tx-123",
        AccountType: LedgerAccountType.WALLET
    );

    [Fact]
    public void Valid_ShouldPass() =>
        _validator.TestValidate(ValidDto()).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void Amount_Zero_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { Amount = 0 })
            .ShouldHaveValidationErrorFor(x => x.Amount);

    [Fact]
    public void Description_Empty_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { Description = "" })
            .ShouldHaveValidationErrorFor(x => x.Description);

    [Fact]
    public void TransactionId_Empty_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { TransactionId = "" })
            .ShouldHaveValidationErrorFor(x => x.TransactionId);

    [Fact]
    public void BalanceType_Invalid_ShouldFail() =>
        _validator.TestValidate(ValidDto() with { BalanceType = "INVALID" })
            .ShouldHaveValidationErrorFor(x => x.BalanceType);

    [Theory]
    [InlineData("AVAILABLE")]
    [InlineData("WAITING")]
    public void BalanceType_Valid_ShouldPass(string type) =>
        _validator.TestValidate(ValidDto() with { BalanceType = type })
            .ShouldNotHaveValidationErrorFor(x => x.BalanceType);
}
