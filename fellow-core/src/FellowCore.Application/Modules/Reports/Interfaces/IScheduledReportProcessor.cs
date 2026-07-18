namespace FellowCore.Application.Modules.Reports.Interfaces;

public interface IScheduledReportProcessor
{
    Task ProcessDueReportsAsync(CancellationToken ct = default);
}
