using FluentValidation;
using FellowCore.Application.Modules.Tenants.DTOs;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Tenants.Validators;

public class CreateTenantDtoValidator : AbstractValidator<CreateTenantDto>
{
    public CreateTenantDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("O nome do tenant é obrigatório.")
            .MaximumLength(100).WithMessage("O nome deve ter no máximo 100 caracteres.");

        RuleFor(x => x.Slug)
            .NotEmpty().WithMessage("O slug do tenant é obrigatório.")
            .Matches(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")
            .WithMessage("O slug deve conter apenas letras minúsculas, números e hífens.");
    }
}

public class RotateApiKeyDtoValidator : AbstractValidator<RotateApiKeyDto>
{
    public RotateApiKeyDtoValidator()
    {
        RuleFor(x => x.CurrentApiSecret)
            .NotEmpty().WithMessage("O secret atual é obrigatório.")
            .MaximumLength(256);
    }
}

public class UpdateTenantProvidersDtoValidator : AbstractValidator<UpdateTenantProvidersDto>
{
    public UpdateTenantProvidersDtoValidator()
    {
        RuleFor(x => x.ActivePixProvider)
            .IsInEnum().WithMessage("Provider Pix inválido.")
            .When(x => x.ActivePixProvider.HasValue);

        RuleFor(x => x.ActiveCreditProvider)
            .IsInEnum().WithMessage("Provider de crédito inválido.")
            .When(x => x.ActiveCreditProvider.HasValue);

        RuleFor(x => x)
            .Must(x => x.ActivePixProvider.HasValue || x.ActiveCreditProvider.HasValue)
            .WithMessage("Informe ao menos um provider para atualizar.");
    }
}
