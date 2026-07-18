using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class SplitRuleRepository(AppDbContext context) : ISplitRuleRepository
{
    public void Add(SplitRule rule) => context.SplitRules.Add(rule);
    public void Update(SplitRule rule) => context.SplitRules.Update(rule);

    public async Task<SplitRule?> GetByIdAsync(Guid tenantId, Guid id)
    {
        return await context.SplitRules
            .FirstOrDefaultAsync(sr => sr.TenantId == tenantId && sr.Id == id);
    }

    public async Task<SplitRule?> GetByIdWithRecipientsAsync(Guid tenantId, Guid id)
    {
        return await context.SplitRules
            .Include(sr => sr.Recipients)
            .FirstOrDefaultAsync(sr => sr.TenantId == tenantId && sr.Id == id);
    }

    public async Task<(IReadOnlyList<SplitRule> Items, int TotalCount)> GetPagedAsync(Guid tenantId, int skip, int take)
    {
        var query = context.SplitRules
            .Include(sr => sr.Recipients)
            .Where(sr => sr.TenantId == tenantId);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(sr => sr.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<(IReadOnlyList<SplitRule> Items, int TotalCount)> GetPagedByRecipientAsync(Guid tenantId, Guid sellerId, int skip, int take)
    {
        var query = context.SplitRules
            .Include(sr => sr.Recipients)
            .Where(sr => sr.TenantId == tenantId && sr.Recipients.Any(r => r.SellerId == sellerId));

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(sr => sr.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<(IReadOnlyList<SplitRule> Items, int TotalCount)> GetPagedByOwnerOrRecipientAsync(Guid tenantId, Guid sellerId, int skip, int take)
    {
        var query = context.SplitRules
            .Include(sr => sr.Recipients)
            .Where(sr => sr.TenantId == tenantId
                && (sr.OwnerSellerId == sellerId
                    || sr.Recipients.Any(r => r.SellerId == sellerId)));

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(sr => sr.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<bool> ExistsByNameAsync(Guid tenantId, string name)
    {
        return await context.SplitRules
            .AnyAsync(sr => sr.TenantId == tenantId && sr.Name == name && sr.IsActive);
    }

    public async Task SaveChangesAsync()
    {
        await context.SaveChangesAsync();
    }
}
