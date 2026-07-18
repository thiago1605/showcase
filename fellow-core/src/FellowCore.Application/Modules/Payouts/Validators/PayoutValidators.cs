using FluentValidation;
using FellowCore.Application.Modules.Payouts.DTOs;

namespace FellowCore.Application.Modules.Payouts.Validators;

public class CreatePayoutDtoValidator : AbstractValidator<CreatePayoutDto>
{
    public CreatePayoutDtoValidator()
    {
        RuleFor(x => x.SellerId)
            .NotEmpty().WithMessage("O ID do seller é obrigatório.");

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor deve ser maior que zero.")
            .LessThanOrEqualTo(999_999.99m).WithMessage("O valor maximo e R$ 999.999,99.");
    }
}
