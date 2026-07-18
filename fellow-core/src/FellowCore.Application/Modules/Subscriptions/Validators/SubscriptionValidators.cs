using FluentValidation;
using FellowCore.Application.Modules.Subscriptions.DTOs;

namespace FellowCore.Application.Modules.Subscriptions.Validators;

public class CreateSubscriptionDtoValidator : AbstractValidator<CreateSubscriptionDto>
{
    public CreateSubscriptionDtoValidator()
    {
        RuleFor(x => x.SellerId)
            .NotEmpty().WithMessage("O ID do seller é obrigatório.");

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor da assinatura deve ser maior que zero.")
            .LessThanOrEqualTo(999_999.99m).WithMessage("O valor maximo e R$ 999.999,99.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("A descricao e obrigatoria.")
            .MaximumLength(500).WithMessage("A descricao deve ter no maximo 500 caracteres.");

        RuleFor(x => x.Interval)
            .IsInEnum().WithMessage("Intervalo de cobranca invalido.");

        RuleFor(x => x.MaxCycles)
            .GreaterThan(0).WithMessage("O numero maximo de ciclos deve ser maior que zero.")
            .LessThanOrEqualTo(1000).WithMessage("O numero maximo de ciclos e 1000.")
            .When(x => x.MaxCycles.HasValue);
    }
}
