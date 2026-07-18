using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Reports.Interfaces;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public class ScheduledReportProcessor(
    IScheduledReportRepository reportRepository,
    IExportService exportService,
    IEmailService emailService,
    ILogger<ScheduledReportProcessor> logger) : IScheduledReportProcessor
{
    public async Task ProcessDueReportsAsync(CancellationToken ct = default)
    {
        var dueReports = await reportRepository.GetDueReportsAsync(DateTime.UtcNow);

        if (dueReports.Count == 0) return;

        logger.LogInformation("Processando {Count} relatório(s) agendado(s)", dueReports.Count);

        foreach (var report in dueReports)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var (data, contentType, extension) = await GenerateReportAsync(report);
                var periodLabel = report.Frequency switch
                {
                    ReportFrequency.DAILY => "Diário",
                    ReportFrequency.WEEKLY => "Semanal",
                    ReportFrequency.MONTHLY => "Mensal",
                    _ => "Periódico"
                };
                var reportLabel = report.ReportType == ReportType.TRANSACTIONS ? "Transações" : "Saques";
                var subject = $"Relatório {periodLabel} de {reportLabel} — Fellow Pay";
                var fileName = $"{report.ReportType.ToString().ToLower()}_{DateTime.UtcNow:yyyyMMdd}.{extension}";

                var htmlBody = $"""
                    <p>Olá,</p>
                    <p>Segue em anexo o relatório <strong>{periodLabel.ToLower()}</strong> de <strong>{reportLabel.ToLower()}</strong>.</p>
                    <p>Período: até {DateTime.UtcNow:dd/MM/yyyy}</p>
                    <p>—<br/>Fellow Pay</p>
                    """;

                foreach (var recipient in report.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var message = new EmailMessage(
                        To: recipient,
                        ToName: recipient,
                        Subject: subject,
                        HtmlBody: htmlBody,
                        Attachments: [new EmailAttachment(fileName, data, contentType)]);

                    await emailService.SendAsync(message, ct);
                }

                report.MarkSent();
                logger.LogInformation("Relatório {ReportId} enviado para {Recipients}", report.Id, report.Recipients);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao processar relatório {ReportId}", report.Id);
            }
        }

        await reportRepository.SaveChangesAsync();
    }

    private async Task<(byte[] Data, string ContentType, string Extension)> GenerateReportAsync(Domain.Entities.ScheduledReport report)
    {
        var from = report.Frequency switch
        {
            ReportFrequency.DAILY => DateTime.UtcNow.Date.AddDays(-1),
            ReportFrequency.WEEKLY => DateTime.UtcNow.Date.AddDays(-7),
            ReportFrequency.MONTHLY => DateTime.UtcNow.Date.AddMonths(-1),
            _ => DateTime.UtcNow.Date.AddDays(-1)
        };
        var to = DateTime.UtcNow;

        return (report.ReportType, report.Format) switch
        {
            (ReportType.TRANSACTIONS, ReportFormat.CSV) =>
                (await exportService.ExportTransactionsCsvAsync(report.TenantId, from, to, null, null, null, null), "text/csv", "csv"),
            (ReportType.TRANSACTIONS, ReportFormat.PDF) =>
                (await exportService.ExportTransactionsPdfAsync(report.TenantId, from, to, null, null, null, null), "application/pdf", "pdf"),
            (ReportType.PAYOUTS, ReportFormat.CSV) =>
                (await exportService.ExportPayoutsCsvAsync(report.TenantId, from, to, null, null), "text/csv", "csv"),
            (ReportType.PAYOUTS, ReportFormat.PDF) =>
                (await exportService.ExportPayoutsPdfAsync(report.TenantId, from, to, null, null), "application/pdf", "pdf"),
            _ => throw new InvalidOperationException($"Tipo de relatório não suportado: {report.ReportType}/{report.Format}")
        };
    }
}
