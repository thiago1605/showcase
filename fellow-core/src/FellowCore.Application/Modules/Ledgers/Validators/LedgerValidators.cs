using FluentValidation;
using FellowCore.Application.Modules.Ledgers.DTOs;

namespace FellowCore.Application.Modules.Ledgers.Validators;

public class CreateLedgerCreditDtoValidator : AbstractValidator<CreateLedgerCreditDto>
{
    public CreateLedgerCreditDtoValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor do credito deve ser positivo.")
            .LessThanOrEqualTo(999_999.99m).WithMessage("O valor maximo e R$ 999.999,99.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("A descrição é obrigatória.");

        RuleFor(x => x.TransactionId)
            .NotEmpty().WithMessage("O ID da transação é obrigatório.");

        RuleFor(x => x.AccountType)
            .IsInEnum().WithMessage("Tipo de conta inválido.");

        RuleFor(x => x.BalanceType)
            .Matches(@"^(AVAILABLE|WAITING)$").WithMessage("O balanceType deve ser 'AVAILABLE' ou 'WAITING'.")
            .When(x => x.BalanceType != null);
    }
}
