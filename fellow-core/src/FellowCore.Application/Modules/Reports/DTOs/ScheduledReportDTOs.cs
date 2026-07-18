using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Reports.DTOs;

public sealed record CreateScheduledReportDto(
    ReportType ReportType,
    ReportFormat Format,
    ReportFrequency Frequency,
    string Recipients);

public sealed record ScheduledReportResponse(
    Guid Id,
    ReportType ReportType,
    ReportFormat Format,
    ReportFrequency Frequency,
    string Recipients,
    bool Enabled,
    DateTime? LastSentAt,
    DateTime NextRunAt,
    DateTime CreatedAt);
