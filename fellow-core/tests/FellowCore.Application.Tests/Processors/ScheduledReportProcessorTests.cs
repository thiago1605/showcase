using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Processors;

public class ScheduledReportProcessorTests
{
    private readonly IScheduledReportRepository _reportRepository = Substitute.For<IScheduledReportRepository>();
    private readonly IExportService _exportService = Substitute.For<IExportService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly ILogger<ScheduledReportProcessor> _logger = Substitute.For<ILogger<ScheduledReportProcessor>>();
    private readonly ScheduledReportProcessor _sut;

    public ScheduledReportProcessorTests()
    {
        _sut = new ScheduledReportProcessor(_reportRepository, _exportService, _emailService, _logger);
    }

    [Fact]
    public async Task ProcessDueReportsAsync_ShouldDoNothing_WhenNoReportsDue()
    {
        // Arrange
        _reportRepository.GetDueReportsAsync(Arg.Any<DateTime>())
            .Returns(new List<ScheduledReport>());

        // Act
        await _sut.ProcessDueReportsAsync();

        // Assert
        await _exportService.DidNotReceive().ExportTransactionsCsvAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<Guid?>(), Arg.Any<TransactionStatus?>(), Arg.Any<PaymentType?>(), Arg.Any<PaymentProvider?>());
        await _emailService.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await _reportRepository.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessDueReportsAsync_ShouldGenerateCsvAndSendEmail_ForTransactionsCsvReport()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var report = ScheduledReport.Create(tenantId, ReportType.TRANSACTIONS, ReportFormat.CSV, ReportFrequency.DAILY, "user@test.com");

        _reportRepository.GetDueReportsAsync(Arg.Any<DateTime>())
            .Returns(new List<ScheduledReport> { report });

        var csvData = "header1,header2\nvalue1,value2"u8.ToArray();
        _exportService.ExportTransactionsCsvAsync(
                tenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                null, null, null, null)
            .Returns(csvData);

        // Act
        await _sut.ProcessDueReportsAsync();

        // Assert
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.To == "user@test.com" &&
                m.Subject.Contains("Diário") &&
                m.Subject.Contains("Transações") &&
                m.Attachments != null &&
                m.Attachments.Count == 1 &&
                m.Attachments[0].ContentType == "text/csv" &&
                m.Attachments[0].FileName.EndsWith(".csv")),
            Arg.Any<CancellationToken>());

        report.LastSentAt.Should().NotBeNull();
        await _reportRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessDueReportsAsync_ShouldGeneratePdf_ForTransactionsPdfReport()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var report = ScheduledReport.Create(tenantId, ReportType.TRANSACTIONS, ReportFormat.PDF, ReportFrequency.WEEKLY, "user@test.com");

        _reportRepository.GetDueReportsAsync(Arg.Any<DateTime>())
            .Returns(new List<ScheduledReport> { report });

        var pdfData = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF
        _exportService.ExportTransactionsPdfAsync(
                tenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                null, null, null, null)
            .Returns(pdfData);

        // Act
        await _sut.ProcessDueReportsAsync();

        // Assert
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.Subject.Contains("Semanal") &&
                m.Attachments != null &&
                m.Attachments[0].ContentType == "application/pdf" &&
                m.Attachments[0].FileName.EndsWith(".pdf")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueReportsAsync_ShouldGenerateCsv_ForPayoutsCsvReport()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var report = ScheduledReport.Create(tenantId, ReportType.PAYOUTS, ReportFormat.CSV, ReportFrequency.MONTHLY, "finance@test.com");

        _reportRepository.GetDueReportsAsync(Arg.Any<DateTime>())
            .Returns(new List<ScheduledReport> { report });

        var csvData = "payout_data"u8.ToArray();
        _exportService.ExportPayoutsCsvAsync(
                tenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                null, null)
            .Returns(csvData);

        // Act
        await _sut.ProcessDueReportsAsync();

        // Assert
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m =>
                m.To == "finance@test.com" &&
                m.Subject.Contains("Mensal") &&
                m.Subject.Contains("Saques")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueReportsAsync_ShouldSendToMultipleRecipients()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var report = ScheduledReport.Create(tenantId, ReportType.TRANSACTIONS, ReportFormat.CSV, ReportFrequency.DAILY, "user1@test.com;user2@test.com");

        _reportRepository.GetDueReportsAsync(Arg.Any<DateTime>())
            .Returns(new List<ScheduledReport> { report });

        _exportService.ExportTransactionsCsvAsync(
                tenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                null, null, null, null)
            .Returns("data"u8.ToArray());

        // Act
        await _sut.ProcessDueReportsAsync();

        // Assert
        await _emailService.Received(2).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "user1@test.com"), Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "user2@test.com"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessDueReportsAsync_ShouldContinueProcessing_WhenOneReportFails()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var report1 = ScheduledReport.Create(tenantId, ReportType.TRANSACTIONS, ReportFormat.CSV, ReportFrequency.DAILY, "user@test.com");
        var report2 = ScheduledReport.Create(tenantId, ReportType.PAYOUTS, ReportFormat.CSV, ReportFrequency.DAILY, "user2@test.com");

        _reportRepository.GetDueReportsAsync(Arg.Any<DateTime>())
            .Returns(new List<ScheduledReport> { report1, report2 });

        // First report's export throws
        _exportService.ExportTransactionsCsvAsync(
                tenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                null, null, null, null)
            .ThrowsAsync(new InvalidOperationException("Export failed"));

        // Second report's export succeeds
        _exportService.ExportPayoutsCsvAsync(
                tenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                null, null)
            .Returns("data"u8.ToArray());

        // Act
        await _sut.ProcessDueReportsAsync();

        // Assert
        // First report should NOT be marked as sent
        report1.LastSentAt.Should().BeNull();

        // Second report should be sent and marked
        report2.LastSentAt.Should().NotBeNull();

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "user2@test.com"), Arg.Any<CancellationToken>());

        await _reportRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessDueReportsAsync_ShouldRespectCancellationToken()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var report = ScheduledReport.Create(tenantId, ReportType.TRANSACTIONS, ReportFormat.CSV, ReportFrequency.DAILY, "user@test.com");

        _reportRepository.GetDueReportsAsync(Arg.Any<DateTime>())
            .Returns(new List<ScheduledReport> { report });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        await _sut.ProcessDueReportsAsync(cts.Token);

        // Assert — cancellation is checked before processing first report
        await _exportService.DidNotReceive().ExportTransactionsCsvAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<Guid?>(), Arg.Any<TransactionStatus?>(), Arg.Any<PaymentType?>(), Arg.Any<PaymentProvider?>());
    }

    [Fact]
    public async Task ProcessDueReportsAsync_ShouldMarkReportSent_AndAdvanceNextRunAt()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var report = ScheduledReport.Create(tenantId, ReportType.TRANSACTIONS, ReportFormat.CSV, ReportFrequency.DAILY, "user@test.com");

        _reportRepository.GetDueReportsAsync(Arg.Any<DateTime>())
            .Returns(new List<ScheduledReport> { report });

        _exportService.ExportTransactionsCsvAsync(
                tenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                null, null, null, null)
            .Returns("data"u8.ToArray());

        // Act
        await _sut.ProcessDueReportsAsync();

        // Assert
        report.LastSentAt.Should().NotBeNull();
        // NextRunAt should be recalculated to the next day at 7am UTC
        report.NextRunAt.Should().BeAfter(DateTime.UtcNow);
        report.NextRunAt.Hour.Should().Be(7);
    }
}
