using FluentValidation;
using FellowCore.Api.Controllers;

namespace FellowCore.Api.Validators;

public class CreatePixKeyRequestValidator : AbstractValidator<CreatePixKeyRequest>
{
    private static readonly string[] AllowedTypes = ["CPF", "CNPJ", "EMAIL", "PHONE", "EVP"];

    public CreatePixKeyRequestValidator()
    {
        RuleFor(x => x.Key)
            .NotEmpty().WithMessage("A chave Pix e obrigatoria.")
            .MaximumLength(100).WithMessage("A chave Pix deve ter no maximo 100 caracteres.");

        RuleFor(x => x.Type)
            .NotEmpty().WithMessage("O tipo da chave e obrigatorio.")
            .Must(t => AllowedTypes.Contains(t, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Tipo de chave invalido. Permitidos: CPF, CNPJ, EMAIL, PHONE, EVP.");
    }
}

public class CreatePixTransferRequestValidator : AbstractValidator<CreatePixTransferRequest>
{
    public CreatePixTransferRequestValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor da transferencia deve ser maior que zero.")
            .LessThanOrEqualTo(100_000m).WithMessage("O valor maximo por transferencia e R$ 100.000.");

        RuleFor(x => x.FromPixKey)
            .NotEmpty().WithMessage("A chave Pix de origem e obrigatoria.")
            .MaximumLength(100);

        RuleFor(x => x.ToPixKey)
            .NotEmpty().WithMessage("A chave Pix de destino e obrigatoria.")
            .MaximumLength(100);
    }
}

public class RegisterApplePayDomainRequestValidator : AbstractValidator<RegisterApplePayDomainRequest>
{
    public RegisterApplePayDomainRequestValidator()
    {
        RuleFor(x => x.DomainName)
            .NotEmpty().WithMessage("O nome do dominio e obrigatorio.")
            .MaximumLength(253).WithMessage("O dominio deve ter no maximo 253 caracteres.")
            .Matches(@"^[a-zA-Z0-9][a-zA-Z0-9\.\-]+[a-zA-Z0-9]$")
            .WithMessage("O dominio deve ser um nome de dominio valido.");
    }
}

public class UpdateTransactionDtoValidator : AbstractValidator<UpdateTransactionDto>
{
    public UpdateTransactionDtoValidator()
    {
        RuleFor(x => x.ExpiresAt)
            .GreaterThan(DateTime.UtcNow).WithMessage("A data de expiracao deve ser futura.");
    }
}

public class ResolveIssueRequestValidator : AbstractValidator<ResolveIssueRequest>
{
    public ResolveIssueRequestValidator()
    {
        RuleFor(x => x.Notes)
            .NotEmpty().WithMessage("As notas de resolucao sao obrigatorias.")
            .MaximumLength(2000).WithMessage("As notas devem ter no maximo 2000 caracteres.");
    }
}
