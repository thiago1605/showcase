using FluentValidation;
using FellowCore.Application.Modules.Customers.DTOs;

namespace FellowCore.Application.Modules.Customers.Validators;

public class CreateCustomerDtoValidator : AbstractValidator<CreateCustomerDto>
{
    public CreateCustomerDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("O nome é obrigatório.")
            .MaximumLength(200).WithMessage("O nome deve ter no máximo 200 caracteres.");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("O email é obrigatório.")
            .EmailAddress().WithMessage("Email inválido.");

        RuleFor(x => x.Document)
            .Matches(@"^\d{11,14}$").WithMessage("O documento deve conter entre 11 e 14 dígitos.")
            .When(x => !string.IsNullOrEmpty(x.Document));
    }
}

public class UpdateCustomerDtoValidator : AbstractValidator<UpdateCustomerDto>
{
    public UpdateCustomerDtoValidator()
    {
        RuleFor(x => x.Name)
            .MaximumLength(200).WithMessage("O nome deve ter no maximo 200 caracteres.")
            .When(x => x.Name != null);

        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Email invalido.")
            .When(x => x.Email != null);

        RuleFor(x => x.Document)
            .Matches(@"^\d{11,14}$").WithMessage("O documento deve conter entre 11 e 14 digitos.")
            .When(x => x.Document != null);
    }
}

public class AddPaymentMethodDtoValidator : AbstractValidator<AddPaymentMethodDto>
{
    public AddPaymentMethodDtoValidator()
    {
        RuleFor(x => x.Type)
            .IsInEnum().WithMessage("Tipo de pagamento inválido.");

        RuleFor(x => x.Token)
            .NotEmpty().WithMessage("O token é obrigatório.");

        RuleFor(x => x.Gateway)
            .IsInEnum().WithMessage("Gateway inválido.");

        RuleFor(x => x.First6)
            .NotEmpty().WithMessage("Os primeiros 6 dígitos são obrigatórios.")
            .Matches(@"^\d{6}$").WithMessage("First6 deve conter exatamente 6 dígitos.");

        RuleFor(x => x.Last4)
            .NotEmpty().WithMessage("Os últimos 4 dígitos são obrigatórios.")
            .Matches(@"^\d{4}$").WithMessage("Last4 deve conter exatamente 4 dígitos.");

        RuleFor(x => x.Brand)
            .NotEmpty().WithMessage("A bandeira é obrigatória.");

        RuleFor(x => x.Expiration)
            .NotEmpty().WithMessage("A data de expiração é obrigatória.");

        RuleFor(x => x.HolderName)
            .NotEmpty().WithMessage("O nome do titular é obrigatório.");
    }
}
