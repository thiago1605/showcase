using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class SplitAllocationRepository(AppDbContext context) : ISplitAllocationRepository
{
    public async Task<List<SplitAllocation>> GetByTransactionIdAsync(Guid tenantId, Guid transactionId)
    {
        return await context.SplitAllocations
            .Where(a => a.TenantId == tenantId && a.TransactionId == transactionId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
    }

    public async Task AddRangeAsync(IEnumerable<SplitAllocation> allocations)
    {
        await context.SplitAllocations.AddRangeAsync(allocations);
    }

    public async Task SaveChangesAsync()
    {
        await context.SaveChangesAsync();
    }
}
