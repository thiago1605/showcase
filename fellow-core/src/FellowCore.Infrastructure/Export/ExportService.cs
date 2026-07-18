using System.Text;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FellowCore.Infrastructure.Export;

public class ExportService(ITransactionRepository transactionRepo, IPayoutRepository payoutRepo) : IExportService
{
    // ── CSV ──────────────────────────────────────────────────────────────────

    public async Task<byte[]> ExportTransactionsCsvAsync(
        Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId,
        TransactionStatus? status, PaymentType? paymentType, PaymentProvider? provider)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Data,Tipo,Provedor,Status,Valor,Taxa,Valor Liquido,Parcelas,Descricao,ProviderTxId");
        await foreach (var t in transactionRepo.StreamForExportAsync(tenantId, from, to, sellerId, status, paymentType, provider))
        {
            sb.AppendLine(string.Join(",",
                t.Id,
                t.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                t.PaymentType,
                t.Provider,
                t.Status,
                t.Amount.ToString("F2"),
                t.FeeAmount?.ToString("F2") ?? "",
                t.NetAmount?.ToString("F2") ?? "",
                t.Installments,
                CsvEscape(t.Description),
                CsvEscape(t.ProviderTxId)));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public async Task<byte[]> ExportPayoutsCsvAsync(
        Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, PayoutStatus? status)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Data,Seller,Valor,Taxa,Status,DataProcessamento,IdBancario,MotivoFalha");
        await foreach (var p in payoutRepo.StreamForExportAsync(tenantId, from, to, sellerId, status))
        {
            sb.AppendLine(string.Join(",",
                p.Id,
                p.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                CsvEscape(p.Seller?.LegalName),
                p.Amount.ToString("F2"),
                p.Fee.ToString("F2"),
                p.Status,
                p.ProcessedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                CsvEscape(p.BankTransactionId),
                CsvEscape(p.FailureReason)));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // ── PDF ──────────────────────────────────────────────────────────────────

    public async Task<byte[]> ExportTransactionsPdfAsync(
        Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId,
        TransactionStatus? status, PaymentType? paymentType, PaymentProvider? provider)
    {
        var transactions = await transactionRepo.GetForExportAsync(tenantId, from, to, sellerId, status, paymentType, provider);
        var totalAmount = transactions.Sum(t => t.Amount);
        var totalNet = transactions.Sum(t => t.NetAmount ?? 0);
        var dateRange = FormatDateRange(from, to);

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Element(ComposeHeader("Relatório de Transações", dateRange));
                page.Content().Element(content =>
                {
                    content.Column(col =>
                    {
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.RelativeColumn(3);  // Id
                                columns.RelativeColumn(2.5f); // Data
                                columns.RelativeColumn(1.5f); // Tipo
                                columns.RelativeColumn(2);   // Provedor
                                columns.RelativeColumn(2);   // Status
                                columns.RelativeColumn(1.5f); // Valor
                                columns.RelativeColumn(1.5f); // Taxa
                                columns.RelativeColumn(1.5f); // Líquido
                                columns.RelativeColumn(1);   // Parcelas
                            });

                            table.Header(header =>
                            {
                                foreach (var h in new[] { "ID", "Data", "Tipo", "Provedor", "Status", "Valor", "Taxa", "Líquido", "Parcelas" })
                                    header.Cell().Background("#1a1a2e").Padding(4).Text(h).FontColor(Colors.White).Bold().FontSize(8);
                            });

                            var alternate = false;
                            foreach (var t in transactions)
                            {
                                var bg = alternate ? "#f5f5f5" : "#ffffff";
                                alternate = !alternate;
                                var cells = new[]
                                {
                                    t.Id.ToString()[..8] + "…",
                                    t.CreatedAt.ToString("dd/MM/yy HH:mm"),
                                    t.PaymentType.ToString(),
                                    t.Provider.ToString(),
                                    t.Status.ToString(),
                                    $"R$ {t.Amount:F2}",
                                    t.FeeAmount.HasValue ? $"R$ {t.FeeAmount:F2}" : "-",
                                    t.NetAmount.HasValue ? $"R$ {t.NetAmount:F2}" : "-",
                                    t.Installments.ToString()
                                };
                                foreach (var cell in cells)
                                    table.Cell().Background(bg).Padding(4).Text(cell).FontSize(8);
                            }
                        });

                        col.Item().PaddingTop(10).Row(row =>
                        {
                            row.AutoItem().Text($"Total de registros: {transactions.Count}").Bold();
                            row.RelativeItem();
                            row.AutoItem().Text($"Total bruto: R$ {totalAmount:F2}  |  Total líquido: R$ {totalNet:F2}").Bold();
                        });
                    });
                });

                page.Footer().Element(ComposeFooter);
            });
        });

        return doc.GeneratePdf();
    }

    public async Task<byte[]> ExportPayoutsPdfAsync(
        Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, PayoutStatus? status)
    {
        var payouts = await payoutRepo.GetForExportAsync(tenantId, from, to, sellerId, status);
        var totalAmount = payouts.Sum(p => p.Amount);
        var totalFees = payouts.Sum(p => p.Fee);
        var dateRange = FormatDateRange(from, to);

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Element(ComposeHeader("Relatório de Saques (Payouts)", dateRange));
                page.Content().Element(content =>
                {
                    content.Column(col =>
                    {
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.RelativeColumn(3);
                                columns.RelativeColumn(2.5f);
                                columns.RelativeColumn(3);
                                columns.RelativeColumn(1.5f);
                                columns.RelativeColumn(1.5f);
                                columns.RelativeColumn(2);
                                columns.RelativeColumn(2.5f);
                            });

                            table.Header(header =>
                            {
                                foreach (var h in new[] { "ID", "Data", "Seller", "Valor", "Taxa", "Status", "Processado em" })
                                    header.Cell().Background("#1a1a2e").Padding(4).Text(h).FontColor(Colors.White).Bold().FontSize(8);
                            });

                            var alternate = false;
                            foreach (var p in payouts)
                            {
                                var bg = alternate ? "#f5f5f5" : "#ffffff";
                                alternate = !alternate;
                                var cells = new[]
                                {
                                    p.Id.ToString()[..8] + "…",
                                    p.CreatedAt.ToString("dd/MM/yy HH:mm"),
                                    p.Seller?.LegalName ?? "-",
                                    $"R$ {p.Amount:F2}",
                                    $"R$ {p.Fee:F2}",
                                    p.Status.ToString(),
                                    p.ProcessedAt?.ToString("dd/MM/yy HH:mm") ?? "-"
                                };
                                foreach (var cell in cells)
                                    table.Cell().Background(bg).Padding(4).Text(cell).FontSize(8);
                            }
                        });

                        col.Item().PaddingTop(10).Row(row =>
                        {
                            row.AutoItem().Text($"Total de registros: {payouts.Count}").Bold();
                            row.RelativeItem();
                            row.AutoItem().Text($"Total saques: R$ {totalAmount:F2}  |  Total taxas: R$ {totalFees:F2}").Bold();
                        });
                    });
                });

                page.Footer().Element(ComposeFooter);
            });
        });

        return doc.GeneratePdf();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Action<IContainer> ComposeHeader(string title, string dateRange) =>
        header => header
            .BorderBottom(1).BorderColor("#cccccc")
            .PaddingBottom(6)
            .Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("Fellow Pay").Bold().FontSize(14).FontColor("#1a1a2e");
                    col.Item().Text(title).FontSize(11);
                });
                row.AutoItem().Column(col =>
                {
                    col.Item().Text($"Período: {dateRange}").AlignRight();
                    col.Item().Text($"Gerado em: {DateTime.UtcNow:dd/MM/yyyy HH:mm} UTC").AlignRight().FontColor("#888888");
                });
            });

    private static void ComposeFooter(IContainer footer) =>
        footer
            .BorderTop(1).BorderColor("#cccccc")
            .PaddingTop(4)
            .Row(row =>
            {
                row.RelativeItem().Text("Fellow Pay — Relatório confidencial. Não compartilhe.").FontColor("#888888").FontSize(8);
                row.AutoItem().Text(text =>
                {
                    text.Span("Página ").FontSize(8);
                    text.CurrentPageNumber().FontSize(8);
                    text.Span(" de ").FontSize(8);
                    text.TotalPages().FontSize(8);
                });
            });

    private static string FormatDateRange(DateTime? from, DateTime? to)
    {
        if (from == null && to == null) return "Todos os períodos";
        if (from == null) return $"até {to:dd/MM/yyyy}";
        if (to == null) return $"a partir de {from:dd/MM/yyyy}";
        return $"{from:dd/MM/yyyy} – {to:dd/MM/yyyy}";
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // Neutralize formula injection: prefix dangerous start chars with a single quote
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            value = $"'{value}";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
