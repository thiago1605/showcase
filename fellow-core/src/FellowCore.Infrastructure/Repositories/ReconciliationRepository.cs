using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class ReconciliationRepository(AppDbContext context) : IReconciliationRepository
{
    public void AddRun(ReconciliationRun run) => context.ReconciliationRuns.Add(run);

    public void AddIssue(ReconciliationIssue issue) => context.ReconciliationIssues.Add(issue);

    public async Task<ReconciliationRun?> GetLatestRunAsync(Guid tenantId, string runType)
    {
        return await context.ReconciliationRuns
            .Where(r => r.TenantId == tenantId && r.RunType == runType)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<ReconciliationIssue?> GetIssueByIdAsync(Guid tenantId, Guid issueId)
    {
        return await context.ReconciliationIssues
            .FirstOrDefaultAsync(i => i.Id == issueId && i.TenantId == tenantId);
    }

    public async Task<List<ReconciliationIssue>> GetOpenIssuesAsync(Guid tenantId, int limit = 100)
    {
        return await context.ReconciliationIssues
            .Where(i => i.TenantId == tenantId && i.Resolution == "OPEN")
            .OrderByDescending(i => i.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<ReconciliationIssue>> GetIssuesAsync(Guid tenantId, string? resolution = null, string? severity = null, int limit = 100, int offset = 0)
    {
        var query = context.ReconciliationIssues.Where(i => i.TenantId == tenantId);

        if (!string.IsNullOrEmpty(resolution))
            query = query.Where(i => i.Resolution == resolution);

        if (!string.IsNullOrEmpty(severity))
            query = query.Where(i => i.Severity == severity);

        return await query
            .OrderByDescending(i => i.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<ReconciliationRun>> GetRecentRunsAsync(Guid tenantId, int limit = 10)
    {
        return await context.ReconciliationRuns
            .Where(r => r.TenantId == tenantId)
            .Include(r => r.Issues)
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> GetOpenIssueCountAsync(Guid tenantId)
    {
        return await context.ReconciliationIssues
            .CountAsync(i => i.TenantId == tenantId && i.Resolution == "OPEN");
    }

    public async Task SaveChangesAsync() => await context.SaveChangesAsync();
}
