using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class TransactionItemRepository(AppDbContext context) : ITransactionItemRepository
{
    public async Task<List<TransactionItem>> GetByTransactionIdAsync(Guid tenantId, Guid transactionId)
    {
        return await context.TransactionItems
            .Where(i => i.TenantId == tenantId && i.TransactionId == transactionId)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync();
    }

    public async Task AddRangeAsync(IEnumerable<TransactionItem> items)
    {
        await context.TransactionItems.AddRangeAsync(items);
    }
}
