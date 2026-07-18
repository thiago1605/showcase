using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class RefundIntentRepository(AppDbContext _context) : IRefundIntentRepository
{
    public async Task<List<RefundIntent>> GetByTransactionIdAsync(Guid transactionId)
        => await _context.Set<RefundIntent>()
            .Where(r => r.TransactionId == transactionId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

    public async Task<RefundIntent?> GetByIdempotencyKeyAsync(Guid tenantId, string idempotencyKey)
        => await _context.Set<RefundIntent>()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.IdempotencyKey == idempotencyKey);

    public async Task<RefundIntent?> GetByIdAsync(Guid id)
        => await _context.Set<RefundIntent>()
            .Include(r => r.Transaction)
            .FirstOrDefaultAsync(r => r.Id == id);

    public async Task<List<RefundIntent>> GetRetryDueAsync(DateTime utcNow, int limit = 50)
        => await _context.Set<RefundIntent>()
            .Include(r => r.Transaction)
            .Where(r => r.Status == RefundIntentStatus.PROCESSING
                        && r.NextRetryAt != null
                        && r.NextRetryAt <= utcNow
                        && r.AttemptCount < r.MaxRetries)
            .OrderBy(r => r.NextRetryAt)
            .Take(limit)
            .ToListAsync();

    public async Task<int> GetStuckProcessingCountAsync(TimeSpan olderThan)
    {
        var threshold = DateTime.UtcNow - olderThan;
        return await _context.Set<RefundIntent>()
            .CountAsync(r => r.Status == RefundIntentStatus.PROCESSING
                             && r.UpdatedAt < threshold
                             && (r.NextRetryAt == null || r.NextRetryAt < DateTime.UtcNow));
    }

    public async Task<List<RefundIntent>> GetCompletedByTenantAsync(Guid tenantId, DateTime since)
        => await _context.Set<RefundIntent>()
            .Where(r => r.TenantId == tenantId
                        && r.Status == RefundIntentStatus.COMPLETED
                        && r.UpdatedAt >= since)
            .ToListAsync();

    public async Task<(List<RefundIntent> Items, int Total)> ListAsync(
        Guid tenantId,
        Guid? sellerId,
        RefundIntentStatus? status,
        DateTime? from,
        DateTime? to,
        int page,
        int pageSize)
    {
        var query = _context.Set<RefundIntent>()
            .Include(r => r.Transaction)
            .Where(r => r.TenantId == tenantId);

        if (sellerId.HasValue)
            query = query.Where(r => r.Transaction.SellerId == sellerId.Value);
        if (status.HasValue)
            query = query.Where(r => r.Status == status.Value);
        if (from.HasValue)
            query = query.Where(r => r.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(r => r.CreatedAt <= to.Value);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (items, total);
    }

    public void Add(RefundIntent refundIntent) => _context.Set<RefundIntent>().Add(refundIntent);

    public void Update(RefundIntent refundIntent) => _context.Set<RefundIntent>().Update(refundIntent);

    public async Task SaveChangesAsync() => await _context.SaveChangesAsync();
}
