using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class AuditLogRepository(AppDbContext db) : IAuditLogRepository
{
    public async Task AddAsync(AuditLog log)
    {
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync();
    }

    public async Task<(IReadOnlyList<AuditLog> Items, int TotalCount)> ListByTenantAsync(
        Guid tenantId, string? action, int skip, int take)
    {
        var query = db.AuditLogs
            .Where(l => l.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(l => l.Action == action);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        return (items, total);
    }
}
