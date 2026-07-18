using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class PixPaymentRepository(AppDbContext context) : IPixPaymentRepository
{
    public void Add(PixPayment payment) => context.PixPayments.Add(payment);
    public void Update(PixPayment payment) => context.PixPayments.Update(payment);

    public async Task<PixPayment?> GetByIdAsync(Guid tenantId, Guid id)
        => await context.PixPayments.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id);

    public async Task<PixPayment?> GetByCorrelationIdAsync(Guid tenantId, string correlationId)
        => await context.PixPayments.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.CorrelationId == correlationId);

    public async Task<(IEnumerable<PixPayment> Items, int TotalCount)> GetPagedAsync(Guid tenantId, int skip, int take)
    {
        var query = context.PixPayments.Where(p => p.TenantId == tenantId);
        var totalCount = await query.CountAsync();
        var items = await query.OrderByDescending(p => p.CreatedAt).Skip(skip).Take(take).ToListAsync();
        return (items, totalCount);
    }

    public async Task SaveChangesAsync() => await context.SaveChangesAsync();
}
