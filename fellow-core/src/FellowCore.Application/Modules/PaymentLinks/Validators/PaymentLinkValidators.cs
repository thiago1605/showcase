using FluentValidation;
using FellowCore.Application.Modules.PaymentLinks.DTOs;

namespace FellowCore.Application.Modules.PaymentLinks.Validators;

public class CreatePaymentLinkDtoValidator : AbstractValidator<CreatePaymentLinkDto>
{
    public CreatePaymentLinkDtoValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0).WithMessage("O valor deve ser maior que zero.");
        RuleFor(x => x.PaymentType).IsInEnum().WithMessage("Tipo de pagamento inválido.");
        RuleFor(x => x.Installments).InclusiveBetween(1, 12).WithMessage("Parcelas devem ser entre 1 e 12.");
        // null = ilimitado; quando preenchido, precisa estar no range razoável.
        RuleFor(x => x.MaxUses!.Value).InclusiveBetween(1, 10000)
            .When(x => x.MaxUses.HasValue)
            .WithMessage("Máximo de usos deve ser entre 1 e 10.000 quando informado (deixe em branco para ilimitado).");
        RuleFor(x => x.ExpiresAt)
            .GreaterThan(DateTime.UtcNow).When(x => x.ExpiresAt.HasValue)
            .WithMessage("A data de expiração deve ser futura.");
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description != null);

        // Multi-método: aceita 1..4 métodos únicos. Vazio/omitido = single-method (legacy).
        RuleFor(x => x.PaymentTypes!).Must(types => types.Length >= 1 && types.Length <= 4)
            .WithMessage("Selecione entre 1 e 4 métodos de pagamento.")
            .When(x => x.PaymentTypes != null);
        RuleFor(x => x.PaymentTypes!).Must(types => types.Distinct().Count() == types.Length)
            .WithMessage("Métodos de pagamento duplicados.")
            .When(x => x.PaymentTypes != null);
    }
}

// Payer fields are validated per-method at the service layer (see PaymentLinkService.PayAsync).
// Pix/Boleto rails require name + document + email — service throws when missing.
// Card-typed links collect billing details via Stripe Payment Element / wallet, so the
// frontend can call /pay with an empty body to obtain the clientSecret eagerly and render
// the Element wallets-first (no upfront payer form). Format checks still run when supplied.
public class PayPaymentLinkDtoValidator : AbstractValidator<PayPaymentLinkDto>
{
    public PayPaymentLinkDtoValidator()
    {
        RuleFor(x => x.PayerName).MaximumLength(200).When(x => !string.IsNullOrWhiteSpace(x.PayerName));
        RuleFor(x => x.PayerDocument)
            .Matches(@"^\d{11}$|^\d{14}$").WithMessage("Documento deve ser CPF (11 dígitos) ou CNPJ (14 dígitos).")
            .When(x => !string.IsNullOrWhiteSpace(x.PayerDocument));
        RuleFor(x => x.PayerEmail).EmailAddress().MaximumLength(256)
            .When(x => !string.IsNullOrWhiteSpace(x.PayerEmail));
        RuleFor(x => x.PayerPhone).MaximumLength(20).When(x => !string.IsNullOrWhiteSpace(x.PayerPhone));
    }
}
