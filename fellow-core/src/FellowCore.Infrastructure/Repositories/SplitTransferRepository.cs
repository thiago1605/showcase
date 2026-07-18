using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class SplitTransferRepository(AppDbContext context) : ISplitTransferRepository
{
    public void Add(SplitTransfer transfer) => context.SplitTransfers.Add(transfer);
    public void Update(SplitTransfer transfer) => context.SplitTransfers.Update(transfer);

    public async Task<SplitTransfer?> GetByIdAsync(Guid tenantId, Guid id)
    {
        return await context.SplitTransfers
            .FirstOrDefaultAsync(st => st.TenantId == tenantId && st.Id == id);
    }

    public async Task<SplitTransfer?> GetByTransactionAndRecipientAsync(Guid tenantId, Guid transactionId, Guid recipientSellerId, bool isPrimaryShare = false)
    {
        // Prioritize non-FAILED transfers to avoid returning stale FAILED markers
        // when an active (RESERVED/PROCESSING/PAID) record exists
        return await context.SplitTransfers
            .Where(st => st.TenantId == tenantId &&
                         st.TransactionId == transactionId &&
                         st.RecipientSellerId == recipientSellerId &&
                         st.IsPrimaryShare == isPrimaryShare)
            .OrderBy(st => st.Status == SplitTransferStatus.FAILED ? 1 : 0)
            .ThenByDescending(st => st.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<SplitTransfer>> GetByTransactionIdAsync(Guid tenantId, Guid transactionId)
    {
        return await context.SplitTransfers
            .Where(st => st.TenantId == tenantId && st.TransactionId == transactionId)
            .OrderBy(st => st.CreatedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<SplitTransfer>> GetPendingByTransactionIdAsync(Guid transactionId)
    {
        return await context.SplitTransfers
            .Where(st => st.TransactionId == transactionId &&
                         (st.Status == SplitTransferStatus.PENDING || st.Status == SplitTransferStatus.RESERVED))
            .OrderBy(st => st.CreatedAt)
            .ToListAsync();
    }

    public async Task<(int Pending, int Failed)> GetStatusCountsAsync(Guid tenantId)
    {
        int pending = await context.SplitTransfers
            .CountAsync(st => st.TenantId == tenantId &&
                              (st.Status == SplitTransferStatus.PENDING || st.Status == SplitTransferStatus.RESERVED));
        int failed = await context.SplitTransfers
            .CountAsync(st => st.TenantId == tenantId && st.Status == SplitTransferStatus.FAILED);
        return (pending, failed);
    }

    public async Task SaveChangesAsync()
    {
        await context.SaveChangesAsync();
    }
}
