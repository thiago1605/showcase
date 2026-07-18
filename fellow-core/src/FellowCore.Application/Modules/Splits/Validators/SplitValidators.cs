using FluentValidation;
using FellowCore.Application.Modules.Splits.DTOs;

namespace FellowCore.Application.Modules.Splits.Validators;

public class CreateSplitRuleDtoValidator : AbstractValidator<CreateSplitRuleDto>
{
    public CreateSplitRuleDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("O nome da regra de split e obrigatorio.")
            .MaximumLength(200).WithMessage("O nome deve ter no maximo 200 caracteres.");

        RuleFor(x => x.Recipients)
            .NotNull().WithMessage("A lista de destinatarios e obrigatoria.")
            .Must(r => r != null && r.Count > 0).WithMessage("A regra de split deve ter ao menos um destinatario.")
            .Must(r => r == null || r.Count <= 50).WithMessage("A regra de split pode ter no maximo 50 destinatarios.");

        RuleForEach(x => x.Recipients)
            .SetValidator(new SplitRuleRecipientDtoValidator())
            .When(x => x.Recipients != null);

        RuleFor(x => x)
            .Must(x =>
            {
                if (x.Recipients == null || x.Recipients.Count == 0) return true;
                var totalPct = x.Recipients.Where(r => r.Percentage.HasValue).Sum(r => r.Percentage!.Value);
                return totalPct <= 100m;
            })
            .WithMessage("A soma das porcentagens dos destinatarios excede 100%.");
    }
}

public class SplitRuleRecipientDtoValidator : AbstractValidator<SplitRuleRecipientDto>
{
    public SplitRuleRecipientDtoValidator()
    {
        RuleFor(x => x.SellerId)
            .NotEmpty().WithMessage("O SellerId do destinatario e obrigatorio.");

        RuleFor(x => x)
            .Must(x => x.Percentage.HasValue || x.FixedAmount.HasValue)
            .WithMessage("Informe Percentage ou FixedAmount para o destinatario.");

        RuleFor(x => x)
            .Must(x => !(x.Percentage.HasValue && x.FixedAmount.HasValue))
            .WithMessage("Informe apenas Percentage ou FixedAmount, nao ambos.");

        RuleFor(x => x.Percentage)
            .InclusiveBetween(0.01m, 100.0m)
            .When(x => x.Percentage.HasValue)
            .WithMessage("A porcentagem deve estar entre 0.01 e 100.");

        RuleFor(x => x.FixedAmount)
            .GreaterThan(0)
            .When(x => x.FixedAmount.HasValue)
            .WithMessage("O valor fixo deve ser maior que zero.");
    }
}
