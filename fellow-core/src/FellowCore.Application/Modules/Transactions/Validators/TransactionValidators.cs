using FluentValidation;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Domain.Enums;
using Microsoft.Extensions.Configuration;

namespace FellowCore.Application.Modules.Transactions.Validators;

public class CreateTransactionDtoValidator : AbstractValidator<CreateTransactionDto>
{
    // Cap por TX do produto Gateway da Woovi em produção: R$ 800,00 diurno e noturno,
    // conforme política oficial publicada em https://woovi.com/politicas/gateway/
    // (validada 2026-05-19 via fetch direto + chat Iago/suporte confirmando que o cap
    // é elevável via análise de risco operacional). Sandbox NÃO enforça este cap (testes
    // empíricos de R$1.500 e R$5.000 passaram), mas mantemos o default igual à política
    // pra evitar o seller criar charge que liquida em dev e quebra em prod.
    //
    // Override via OpenPix:MaxPixPerTxBrl quando a Woovi elevar o cap pra um tenant
    // específico (post-análise de risco). Sprint 2: mover pra Seller.PixPerTxLimit
    // (cap per-seller, sincronizado periodicamente com a Woovi via API).
    private const decimal DefaultMaxPixPerTxBrl = 800m;

    public CreateTransactionDtoValidator(IConfiguration configuration)
    {
        decimal maxPix = decimal.TryParse(configuration["OpenPix:MaxPixPerTxBrl"], out var v) && v > 0
            ? v
            : DefaultMaxPixPerTxBrl;

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor deve ser maior que zero.")
            .LessThanOrEqualTo(999_999.99m).WithMessage("O valor maximo e R$ 999.999,99.");

        // PIX tem teto por TX imposto pela adquirente (default R$ 800). Acima disso, o
        // seller deve usar boleto ou cartão. Mensagem precisa ser acionável — sem isso,
        // o seller pensa "Fellow Pay quebrou" em vez de "tenho que mudar o método".
        // Cultura explícita pt-BR pra render "R$ 800,00" (não depender do locale do servidor).
        var ptBr = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        string maxPixFmt = string.Format(ptBr, "R$ {0:N2}", maxPix);
        RuleFor(x => x.Amount)
            .LessThanOrEqualTo(maxPix)
            .When(x => x.PaymentType == PaymentType.PIX)
            .WithMessage($"O valor máximo por PIX é {maxPixFmt} (limite do gateway). Para valores maiores, use boleto, cartão de crédito, ou solicite elevação de limite à Fellow Pay.");

        RuleFor(x => x.PaymentType)
            .IsInEnum().WithMessage("Tipo de pagamento inválido.");

        RuleFor(x => x.Installments)
            .GreaterThanOrEqualTo(1).WithMessage("O número de parcelas deve ser no mínimo 1.")
            .LessThanOrEqualTo(24).WithMessage("O número de parcelas não pode exceder 24.");

        // Parcelamento só faz sentido em crédito — Pix/débito/boleto sempre 1×.
        RuleFor(x => x.Installments)
            .Equal(1)
            .When(x => x.PaymentType != PaymentType.CREDIT_CARD)
            .WithMessage("Parcelamento só é permitido em cartão de crédito.");

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("A descrição é obrigatória.")
            .MaximumLength(500).WithMessage("A descrição deve ter no máximo 500 caracteres.");

        // SellerId quando informado precisa ser real (não Guid.Empty).
        RuleFor(x => x.SellerId)
            .NotEqual(Guid.Empty)
            .When(x => x.SellerId.HasValue)
            .WithMessage("SellerId inválido.");

        // ExternalReferenceId tem MaxLength 200 na entity — espelha a constraint pra
        // dar 400 amigável em vez de erro do EF na hora do INSERT.
        RuleFor(x => x.ExternalReferenceId)
            .MaximumLength(200).When(x => !string.IsNullOrEmpty(x.ExternalReferenceId))
            .WithMessage("ExternalReferenceId deve ter no máximo 200 caracteres.");

        RuleFor(x => x.IdempotencyKey)
            .Length(8, 128).When(x => !string.IsNullOrEmpty(x.IdempotencyKey))
            .WithMessage("IdempotencyKey deve ter entre 8 e 128 caracteres.");

        // Items: cada item válido + soma quantity*unitAmount deve casar com Amount.
        RuleForEach(x => x.Items)
            .SetValidator(new TransactionItemDtoValidator())
            .When(x => x.Items != null);

        RuleFor(x => x)
            .Must(x =>
            {
                if (x.Items == null || x.Items.Count == 0) return true;
                var total = x.Items.Sum(i => i.Quantity * i.UnitAmount);
                // Tolerância de 1 centavo pra arredondamento.
                return Math.Abs(total - x.Amount) <= 0.01m;
            })
            .WithMessage("A soma dos itens (Quantity × UnitAmount) não bate com Amount.");

        RuleFor(x => x.Payer)
            .NotNull().WithMessage("Os dados do pagador são obrigatórios.")
            .SetValidator(new PayerDtoValidator());

        RuleForEach(x => x.Splits)
            .SetValidator(new SplitDtoValidator())
            .When(x => x.Splits != null);

        RuleFor(x => x.Splits)
            .Must(splits => splits == null || splits.Count <= 50)
            .WithMessage("Maximo de 50 splits por transacao.");

        // SPL3: Cannot provide both Splits and SplitRuleId
        RuleFor(x => x)
            .Must(x => !(x.Splits is { Count: > 0 } && x.SplitRuleId.HasValue))
            .WithMessage("Informe Splits ou SplitRuleId, nao ambos.");

        // SPL4: FeeAllocationPolicy must be a valid enum value
        RuleFor(x => x.FeeAllocationPolicy)
            .IsInEnum()
            .When(x => x.FeeAllocationPolicy.HasValue)
            .WithMessage("Politica de alocacao de taxas invalida.");

        // NOTE (H4): This validator checks split amounts against gross Amount because netAmount is
        // computed by the rail after fee calculation and is not available at validation time.
        // The service layer (TransactionService.CreateAsync) performs a second check against
        // netAmount and throws a BusinessException if sum(splits) > netAmount.
        RuleFor(x => x)
            .Must(x =>
            {
                if (x.Splits == null || x.Splits.Count == 0) return true;
                var totalPct = x.Splits.Where(s => s.Percentage.HasValue).Sum(s => s.Percentage!.Value);
                if (totalPct > 100m) return false;
                var totalAmt = x.Splits.Where(s => s.Amount.HasValue).Sum(s => s.Amount!.Value);
                if (totalAmt > x.Amount) return false;
                return true;
            })
            .WithMessage("A soma dos splits (Amount ou Percentage) excede o valor da transacao.");
    }
}

public class SplitDtoValidator : AbstractValidator<SplitDto>
{
    public SplitDtoValidator()
    {
        RuleFor(x => x.SellerId).NotEmpty().WithMessage("O SellerId do split é obrigatório.");

        RuleFor(x => x)
            .Must(x => x.Amount.HasValue || x.Percentage.HasValue)
            .WithMessage("Informe Amount ou Percentage para o split.");

        RuleFor(x => x)
            .Must(x => !(x.Amount.HasValue && x.Percentage.HasValue))
            .WithMessage("Informe apenas Amount ou Percentage, não ambos.");

        RuleFor(x => x.Amount)
            .GreaterThan(0).When(x => x.Amount.HasValue)
            .WithMessage("O valor do split deve ser maior que zero.");

        RuleFor(x => x.Percentage)
            .InclusiveBetween(0.01m, 100.0m).When(x => x.Percentage.HasValue)
            .WithMessage("A porcentagem deve estar entre 0.01 e 100.");
    }
}

public class PayerDtoValidator : AbstractValidator<PayerDto>
{
    public PayerDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("O nome do pagador é obrigatório.")
            .MaximumLength(200).WithMessage("O nome deve ter no máximo 200 caracteres.");

        RuleFor(x => x.Document)
            .NotEmpty().WithMessage("O documento do pagador é obrigatório.")
            .Matches(@"^\d{11,14}$").WithMessage("O documento deve conter entre 11 e 14 dígitos.");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("O email do pagador é obrigatório.")
            .EmailAddress().WithMessage("Email inválido.");
    }
}

public class TransactionItemDtoValidator : AbstractValidator<TransactionItemDto>
{
    public TransactionItemDtoValidator()
    {
        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("A descrição do item é obrigatória.")
            .MaximumLength(255).WithMessage("A descrição do item deve ter no máximo 255 caracteres.");

        RuleFor(x => x.Quantity)
            .GreaterThan(0).WithMessage("A quantidade do item deve ser maior que zero.")
            .LessThanOrEqualTo(10_000).WithMessage("Quantidade do item excede o máximo razoável (10.000).");

        RuleFor(x => x.UnitAmount)
            .GreaterThan(0).WithMessage("O valor unitário do item deve ser maior que zero.")
            .LessThanOrEqualTo(999_999.99m).WithMessage("O valor unitário máximo é R$ 999.999,99.");

        RuleFor(x => x.SellerId)
            .NotEqual(Guid.Empty).When(x => x.SellerId.HasValue)
            .WithMessage("SellerId do item inválido.");
    }
}

public class RefundRequestDtoValidator : AbstractValidator<RefundRequestDto>
{
    public RefundRequestDtoValidator()
    {
        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("O valor do reembolso deve ser maior que zero.")
            .LessThanOrEqualTo(999_999.99m).WithMessage("O valor maximo e R$ 999.999,99.")
            .When(x => x.Amount.HasValue);

        RuleFor(x => x.Reason)
            .MaximumLength(500).WithMessage("O motivo deve ter no máximo 500 caracteres.")
            .When(x => x.Reason != null);
    }
}
