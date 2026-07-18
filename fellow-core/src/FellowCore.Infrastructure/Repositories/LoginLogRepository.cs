using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class LoginLogRepository(AppDbContext context) : ILoginLogRepository
{
    public void Add(LoginLog log) => context.Set<LoginLog>().Add(log);

    public async Task<List<LoginLog>> GetByUserAsync(Guid userId, int limit = 50, DateTime? from = null, DateTime? to = null)
    {
        var query = context.Set<LoginLog>().Where(l => l.UserId == userId);
        if (from.HasValue) query = query.Where(l => l.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(l => l.CreatedAt <= to.Value);
        return await query.OrderByDescending(l => l.CreatedAt).Take(limit).ToListAsync();
    }

    public async Task<List<LoginLog>> GetByTenantAsync(Guid tenantId, int page, int pageSize)
        => await context.Set<LoginLog>()
            .Where(l => l.TenantId == tenantId)
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

    public async Task<int> CountByTenantAsync(Guid tenantId)
        => await context.Set<LoginLog>().CountAsync(l => l.TenantId == tenantId);

    public Task SaveChangesAsync() => context.SaveChangesAsync();
}
