using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class SubscriptionRepository(AppDbContext context) : ISubscriptionRepository
{
    public async Task<Subscription?> GetByIdAsync(Guid tenantId, Guid id)
    {
        return await context.Subscriptions
            .Include(s => s.Seller)
            .Include(s => s.Customer)
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == id);
    }

    public async Task<(IReadOnlyList<Subscription> Items, int TotalCount)> GetPagedAsync(
        Guid tenantId, int skip, int take, Guid? sellerId = null, SubscriptionStatus? status = null)
    {
        var query = context.Subscriptions.Where(s => s.TenantId == tenantId);

        if (sellerId.HasValue)
            query = query.Where(s => s.SellerId == sellerId.Value);
        if (status.HasValue)
            query = query.Where(s => s.Status == status.Value);

        var totalCount = await query.CountAsync();
        var items = await query
            .Include(s => s.Seller)
            .Include(s => s.Customer)
            .OrderByDescending(s => s.CreatedAt)
            .Skip(skip).Take(take).ToListAsync();

        return (items, totalCount);
    }

    public async Task<IReadOnlyList<Subscription>> GetDueForBillingAsync(DateTime referenceDate, int batchSize = 100)
    {
        return await context.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.ACTIVE && s.NextBillingDate <= referenceDate)
            .OrderBy(s => s.NextBillingDate)
            .Take(batchSize)
            .Include(s => s.Seller)
            .ToListAsync();
    }

    public void Add(Subscription subscription) => context.Subscriptions.Add(subscription);
    public void Update(Subscription subscription) => context.Subscriptions.Update(subscription);
    public async Task SaveChangesAsync() => await context.SaveChangesAsync();
}
