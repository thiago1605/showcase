using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class ScheduledReportRepository(AppDbContext context) : IScheduledReportRepository
{
    public async Task<List<ScheduledReport>> GetDueReportsAsync(DateTime now)
        => await context.Set<ScheduledReport>()
            .Where(r => r.Enabled && r.NextRunAt <= now)
            .ToListAsync();

    public async Task<List<ScheduledReport>> GetByTenantAsync(Guid tenantId)
        => await context.Set<ScheduledReport>()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(200)
            .ToListAsync();

    public async Task<ScheduledReport?> GetByIdAsync(Guid tenantId, Guid reportId)
        => await context.Set<ScheduledReport>()
            .FirstOrDefaultAsync(r => r.Id == reportId && r.TenantId == tenantId);

    public void Add(ScheduledReport report) => context.Set<ScheduledReport>().Add(report);
    public void Update(ScheduledReport report) => context.Set<ScheduledReport>().Update(report);
    public Task SaveChangesAsync() => context.SaveChangesAsync();
}
