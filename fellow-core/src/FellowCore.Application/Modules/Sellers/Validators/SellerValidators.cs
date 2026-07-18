using FluentValidation;
using FellowCore.Application.Modules.Sellers.DTOs;

namespace FellowCore.Application.Modules.Sellers.Validators;

public class CreateSellerDtoValidator : AbstractValidator<CreateSellerDto>
{
    public CreateSellerDtoValidator()
    {
        RuleFor(x => x.LegalName)
            .NotEmpty().WithMessage("A razão social é obrigatória.")
            .MaximumLength(100).WithMessage("A razão social deve ter no máximo 100 caracteres.");

        RuleFor(x => x.Document)
            .NotEmpty().WithMessage("O documento é obrigatório.")
            .Matches(@"^\d{11,14}$").WithMessage("O documento deve conter entre 11 e 14 dígitos (CPF ou CNPJ).");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("O email é obrigatório.")
            .EmailAddress().WithMessage("Email inválido.");

        RuleFor(x => x.IncomeValue)
            .GreaterThan(0).WithMessage("O faturamento deve ser maior que zero.");

        RuleFor(x => x.BirthDate)
            .NotEmpty().WithMessage("A data de nascimento é obrigatória.")
            .Matches(@"^\d{4}-\d{2}-\d{2}$").WithMessage("A data deve estar no formato YYYY-MM-DD.");

        RuleFor(x => x.MobilePhone)
            .NotEmpty().WithMessage("O celular é obrigatório.")
            .Matches(@"^\d{10,11}$").WithMessage("O celular deve conter 10 ou 11 dígitos.");

        RuleFor(x => x.Address)
            .NotNull().WithMessage("O endereço é obrigatório.")
            .SetValidator(new SellerAddressDtoValidator());

        RuleFor(x => x.FeeDebit)
            .InclusiveBetween(0, 100).WithMessage("A taxa de débito deve estar entre 0% e 100%.")
            .When(x => x.FeeDebit.HasValue);

        RuleFor(x => x.FeeCreditCash)
            .InclusiveBetween(0, 100).WithMessage("A taxa de crédito à vista deve estar entre 0% e 100%.")
            .When(x => x.FeeCreditCash.HasValue);

        RuleFor(x => x.FeeCreditInstallment)
            .InclusiveBetween(0, 100).WithMessage("A taxa de crédito parcelado deve estar entre 0% e 100%.")
            .When(x => x.FeeCreditInstallment.HasValue);

        RuleFor(x => x.FeePixIn)
            .InclusiveBetween(0, 100).WithMessage("A taxa Pix deve estar entre 0% e 100%.")
            .When(x => x.FeePixIn.HasValue);

        RuleFor(x => x.PayoutFixedFee)
            .GreaterThanOrEqualTo(0).WithMessage("A taxa fixa de saque não pode ser negativa.")
            .When(x => x.PayoutFixedFee.HasValue);

        RuleForEach(x => x.Documents)
            .SetValidator(new SellerDocumentDtoValidator())
            .When(x => x.Documents != null);
    }
}

public class UpdateSellerDtoValidator : AbstractValidator<UpdateSellerDto>
{
    public UpdateSellerDtoValidator()
    {
        RuleFor(x => x.Email)
            .EmailAddress().WithMessage("Email invalido.")
            .When(x => x.Email != null);

        RuleFor(x => x.TradeName)
            .MaximumLength(100).WithMessage("O nome fantasia deve ter no maximo 100 caracteres.")
            .When(x => x.TradeName != null);

        RuleFor(x => x.MobilePhone)
            .Matches(@"^\d{10,11}$").WithMessage("O celular deve conter 10 ou 11 digitos.")
            .When(x => x.MobilePhone != null);

        RuleFor(x => x.WebhookUrl)
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            .WithMessage("A URL do webhook deve ser uma URL HTTPS válida.")
            .When(x => x.WebhookUrl != null);
    }
}

public class SellerAddressDtoValidator : AbstractValidator<SellerAddressDto>
{
    public SellerAddressDtoValidator()
    {
        RuleFor(x => x.Street)
            .NotEmpty().WithMessage("A rua é obrigatória.");

        RuleFor(x => x.Number)
            .NotEmpty().WithMessage("O número é obrigatório.");

        RuleFor(x => x.Neighborhood)
            .NotEmpty().WithMessage("O bairro é obrigatório.");

        RuleFor(x => x.City)
            .NotEmpty().WithMessage("A cidade é obrigatória.");

        RuleFor(x => x.State)
            .NotEmpty().WithMessage("O estado é obrigatório.")
            .Length(2).WithMessage("O estado deve ter exatamente 2 caracteres.");

        RuleFor(x => x.ZipCode)
            .NotEmpty().WithMessage("O CEP é obrigatório.")
            .Matches(@"^\d{8}$").WithMessage("O CEP deve conter exatamente 8 dígitos.");
    }
}

public class SellerDocumentDtoValidator : AbstractValidator<SellerDocumentDto>
{
    public SellerDocumentDtoValidator()
    {
        RuleFor(x => x.Url)
            .NotEmpty().WithMessage("A URL do documento é obrigatória.");

        RuleFor(x => x.Type)
            .NotEmpty().WithMessage("O tipo do documento é obrigatório.");
    }
}

public class SellerWithdrawRequestDtoValidator : AbstractValidator<SellerWithdrawRequestDto>
{
    public SellerWithdrawRequestDtoValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor do saque deve ser maior que zero.")
            .LessThanOrEqualTo(100_000m).WithMessage("O valor máximo por saque é R$ 100.000.");
    }
}

public class CreateSubAccountDtoValidator : AbstractValidator<CreateSubAccountDto>
{
    public CreateSubAccountDtoValidator()
    {
        RuleFor(x => x.PixKey)
            .NotEmpty().WithMessage("A chave Pix e obrigatoria.")
            .MaximumLength(100);

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("O nome e obrigatorio.")
            .MaximumLength(100);
    }
}

public class SubAccountCreditDebitDtoValidator : AbstractValidator<SubAccountCreditDebitDto>
{
    public SubAccountCreditDebitDtoValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor deve ser maior que zero.")
            .LessThanOrEqualTo(100_000m).WithMessage("O valor maximo e R$ 100.000.");

        RuleFor(x => x.Description)
            .MaximumLength(255)
            .When(x => x.Description != null);
    }
}

public class SubAccountTransferDtoValidator : AbstractValidator<SubAccountTransferDto>
{
    public SubAccountTransferDtoValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor deve ser maior que zero.")
            .LessThanOrEqualTo(100_000m).WithMessage("O valor maximo e R$ 100.000.");

        RuleFor(x => x.FromPixKey)
            .NotEmpty().WithMessage("A chave Pix de origem e obrigatoria.")
            .MaximumLength(100);

        RuleFor(x => x.ToPixKey)
            .NotEmpty().WithMessage("A chave Pix de destino e obrigatoria.")
            .MaximumLength(100);
    }
}

public class SubAccountWithdrawDtoValidator : AbstractValidator<SubAccountWithdrawDto>
{
    public SubAccountWithdrawDtoValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor do saque deve ser maior que zero.")
            .LessThanOrEqualTo(100_000m).WithMessage("O valor maximo por saque e R$ 100.000.");
    }
}
