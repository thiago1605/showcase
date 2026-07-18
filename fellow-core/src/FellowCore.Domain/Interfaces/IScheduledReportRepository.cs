using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IScheduledReportRepository
{
    Task<List<ScheduledReport>> GetDueReportsAsync(DateTime now);
    Task<List<ScheduledReport>> GetByTenantAsync(Guid tenantId);
    Task<ScheduledReport?> GetByIdAsync(Guid tenantId, Guid reportId);
    void Add(ScheduledReport report);
    void Update(ScheduledReport report);
    Task SaveChangesAsync();
}
