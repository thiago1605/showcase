using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class PayoutRepository(AppDbContext context) : IPayoutRepository
{
    public async Task<Payout?> GetByIdAsync(Guid tenantId, Guid id)
    {
        return await context.Payouts
            .Include(p => p.Seller)
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id);
    }

    public async Task<(IReadOnlyList<Payout> Items, int TotalCount)> GetPagedAsync(
        Guid tenantId, int skip, int take, Guid? sellerId = null, PayoutStatus? status = null)
    {
        var query = context.Payouts.Where(p => p.TenantId == tenantId);

        if (sellerId.HasValue)
            query = query.Where(p => p.SellerId == sellerId.Value);
        if (status.HasValue)
            query = query.Where(p => p.Status == status.Value);

        var totalCount = await query.CountAsync();
        var items = await query.OrderByDescending(p => p.CreatedAt).Skip(skip).Take(take).ToListAsync();
        return (items, totalCount);
    }

    public async Task<List<Payout>> GetForExportAsync(
        Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, PayoutStatus? status, int limit = 10000)
    {
        return await BuildExportQuery(tenantId, from, to, sellerId, status, limit).ToListAsync();
    }

    public IAsyncEnumerable<Payout> StreamForExportAsync(
        Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, PayoutStatus? status, int limit = 10000)
    {
        return BuildExportQuery(tenantId, from, to, sellerId, status, limit).AsAsyncEnumerable();
    }

    private IQueryable<Payout> BuildExportQuery(
        Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, PayoutStatus? status, int limit)
    {
        var query = context.Payouts.AsNoTracking().Include(p => p.Seller).Where(p => p.TenantId == tenantId);
        if (from.HasValue) query = query.Where(p => p.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(p => p.CreatedAt <= to.Value);
        if (sellerId.HasValue) query = query.Where(p => p.SellerId == sellerId.Value);
        if (status.HasValue) query = query.Where(p => p.Status == status.Value);
        return query.OrderByDescending(p => p.CreatedAt).Take(limit);
    }

    public async Task<List<Payout>> GetByTenantAndDateRangeAsync(Guid tenantId, DateTime from, DateTime to, PayoutStatus? status = null)
    {
        var query = context.Payouts.Where(p => p.TenantId == tenantId && p.CreatedAt >= from && p.CreatedAt <= to);
        if (status.HasValue) query = query.Where(p => p.Status == status.Value);
        return await query.ToListAsync();
    }

    public async Task<(int Count, decimal TotalAmount)> GetPendingSummaryAsync(Guid tenantId)
    {
        var result = await context.Payouts
            .Where(p => p.TenantId == tenantId && (p.Status == PayoutStatus.PENDING || p.Status == PayoutStatus.PROCESSING))
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Total = g.Sum(p => p.Amount) })
            .FirstOrDefaultAsync();

        return result is not null ? (result.Count, result.Total) : (0, 0m);
    }

    public async Task<Payout?> GetByIdGlobalAsync(Guid id)
    {
        return await context.Payouts
            .Include(p => p.Seller)
            .FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<List<Payout>> GetRetryDueAsync(DateTime utcNow, int limit = 50)
    {
        return await context.Payouts
            .Include(p => p.Seller)
            .Where(p => p.Status == PayoutStatus.PROCESSING
                        && p.NextRetryAt != null
                        && p.NextRetryAt <= utcNow
                        && p.AttemptCount < p.MaxRetries)
            .OrderBy(p => p.NextRetryAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> GetStuckProcessingCountAsync(TimeSpan olderThan)
    {
        var threshold = DateTime.UtcNow - olderThan;
        return await context.Payouts
            .CountAsync(p => p.Status == PayoutStatus.PROCESSING
                             && p.UpdatedAt < threshold
                             && (p.NextRetryAt == null || p.NextRetryAt < DateTime.UtcNow));
    }

    public async Task<decimal> GetTodayTotalGrossAsync(DateTime nowUtc)
    {
        // Soma do bruto do dia (UTC), independente de status exceto FAILED/CANCELED.
        // PENDING (agendado ou em fila), PROCESSING e PAID consomem cap — quando
        // o saque é re-tentado e falha, o status vira FAILED e libera o cap.
        var startOfDay = nowUtc.Date;
        var endOfDay = startOfDay.AddDays(1);
        return await context.Payouts
            .Where(p => p.CreatedAt >= startOfDay
                && p.CreatedAt < endOfDay
                && p.Status != PayoutStatus.FAILED
                && p.Status != PayoutStatus.CANCELED)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
    }

    public async Task<List<Payout>> GetScheduledDueAsync(DateTime nowUtc, int limit = 100)
    {
        // FIFO: ordena por CreatedAt — quem entrou na fila primeiro processa primeiro.
        return await context.Payouts
            .Include(p => p.Seller)
            .Where(p => p.Status == PayoutStatus.PENDING
                && p.ScheduledFor != null
                && p.ScheduledFor <= nowUtc)
            .OrderBy(p => p.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public void Add(Payout payout) => context.Payouts.Add(payout);
    public void Update(Payout payout) => context.Payouts.Update(payout);
    public async Task SaveChangesAsync() => await context.SaveChangesAsync();
}
