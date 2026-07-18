using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class DisputeRepository(AppDbContext _context) : IDisputeRepository
{
    /// <summary>
    /// Looks up a dispute by Stripe's external dispute ID (dp_*).
    /// No TenantId filter is applied because this is called from webhook handlers where the
    /// TenantId is resolved from the linked Transaction, not from the dispute ID itself.
    /// ACCEPTED RISK: Stripe dispute IDs are globally unique by design — a given dp_* value
    /// can only ever belong to one transaction across all tenants, so there is no cross-tenant
    /// data-leak vector here. The caller (WebhooksService) validates the associated transaction's
    /// TenantId before taking any ledger action.
    /// </summary>
    public async Task<Dispute?> GetByExternalIdAsync(string externalDisputeId)
        => await _context.Set<Dispute>()
            .FirstOrDefaultAsync(d => d.ExternalDisputeId == externalDisputeId);

    public async Task<Dispute?> GetByTransactionIdAsync(Guid transactionId)
        => await _context.Set<Dispute>()
            .FirstOrDefaultAsync(d => d.TransactionId == transactionId && d.Status == DisputeStatus.OPEN);

    public async Task<List<Dispute>> GetByTenantAsync(Guid tenantId, DisputeStatus? status = null, int skip = 0, int take = 50)
    {
        var query = _context.Set<Dispute>().Where(d => d.TenantId == tenantId);
        if (status.HasValue)
            query = query.Where(d => d.Status == status.Value);
        return await query.OrderByDescending(d => d.CreatedAt).Skip(skip).Take(take).ToListAsync();
    }

    public async Task<(int Count, decimal ExposureAmount)> GetOpenDisputeSummaryAsync(Guid tenantId)
    {
        var result = await _context.Set<Dispute>()
            .Where(d => d.TenantId == tenantId && d.Status == DisputeStatus.OPEN)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Exposure = g.Sum(d => d.Amount) })
            .FirstOrDefaultAsync();

        return result is not null ? (result.Count, result.Exposure) : (0, 0m);
    }

    public void Add(Dispute dispute) => _context.Set<Dispute>().Add(dispute);

    public void Update(Dispute dispute) => _context.Set<Dispute>().Update(dispute);

    public async Task SaveChangesAsync() => await _context.SaveChangesAsync();
}
