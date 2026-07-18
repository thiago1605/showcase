using FluentValidation;
using FellowCore.Application.Modules.Reports.DTOs;

namespace FellowCore.Application.Modules.Reports.Validators;

public class CreateScheduledReportDtoValidator : AbstractValidator<CreateScheduledReportDto>
{
    public CreateScheduledReportDtoValidator()
    {
        RuleFor(x => x.ReportType).IsInEnum().WithMessage("Tipo de relatório inválido.");
        RuleFor(x => x.Format).IsInEnum().WithMessage("Formato inválido.");
        RuleFor(x => x.Frequency).IsInEnum().WithMessage("Frequência inválida.");

        RuleFor(x => x.Recipients)
            .NotEmpty().WithMessage("Informe ao menos um destinatário.")
            .MaximumLength(1000)
            .Must(BeValidEmailList).WithMessage("Os destinatários devem ser emails válidos separados por ponto e vírgula.");
    }

    private static bool BeValidEmailList(string recipients)
    {
        var emails = recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return emails.Length > 0 && emails.All(e => e.Contains('@') && e.Contains('.'));
    }
}
