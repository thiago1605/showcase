using FluentValidation;
using FellowCore.Application.Modules.PixPayments.DTOs;

namespace FellowCore.Application.Modules.PixPayments.Validators;

public class ValidatePixKeyRequestValidator : AbstractValidator<ValidatePixKeyRequest>
{
    public ValidatePixKeyRequestValidator()
    {
        RuleFor(x => x.PixKey)
            .NotEmpty().WithMessage("A chave Pix é obrigatória.")
            .MaximumLength(100).WithMessage("A chave Pix deve ter no máximo 100 caracteres.");
    }
}

public class CreateStaticQrRequestValidator : AbstractValidator<CreateStaticQrRequest>
{
    public CreateStaticQrRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("O nome do QR code é obrigatório.")
            .MaximumLength(100).WithMessage("O nome deve ter no máximo 100 caracteres.");

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor deve ser maior que zero.")
            .LessThanOrEqualTo(100_000m).WithMessage("O valor máximo é R$ 100.000.")
            .When(x => x.Amount.HasValue);

        RuleFor(x => x.Description)
            .MaximumLength(255)
            .When(x => x.Description != null);
    }
}

public class CreatePixPaymentDtoValidator : AbstractValidator<CreatePixPaymentDto>
{
    public CreatePixPaymentDtoValidator()
    {
        RuleFor(x => x.DestinationPixKey)
            .NotEmpty().WithMessage("A chave Pix de destino é obrigatória.")
            .MaximumLength(100);

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor deve ser maior que zero.")
            .LessThanOrEqualTo(100_000m).WithMessage("O valor máximo por pagamento é R$ 100.000.");

        RuleFor(x => x.Description)
            .MaximumLength(255)
            .When(x => x.Description != null);
    }
}
