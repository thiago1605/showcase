using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class ReceiptRepository(AppDbContext context) : IReceiptRepository
{
    public void Add(Receipt receipt) => context.Set<Receipt>().Add(receipt);

    public async Task<Receipt?> GetByIdAsync(Guid tenantId, Guid id)
        => await context.Set<Receipt>()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id);

    public async Task<Receipt?> GetByTransactionIdAsync(Guid tenantId, Guid transactionId, ReceiptType type)
        => await context.Set<Receipt>()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.TransactionId == transactionId && r.Type == type);

    public async Task<Receipt?> GetByPayoutIdAsync(Guid tenantId, Guid payoutId)
        => await context.Set<Receipt>()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.PayoutId == payoutId && r.Type == ReceiptType.PAYOUT);

    public async Task<Receipt?> GetByRefundIntentIdAsync(Guid tenantId, Guid refundIntentId)
        => await context.Set<Receipt>()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.RefundIntentId == refundIntentId && r.Type == ReceiptType.REFUND);

    public async Task<Receipt?> GetBySplitReceivedAsync(Guid tenantId, Guid transactionId, Guid sellerId)
        => await context.Set<Receipt>()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.TransactionId == transactionId
                && r.SellerId == sellerId && r.Type == ReceiptType.SPLIT_RECEIVED);

    public async Task<List<Receipt>> GetBySellerAsync(Guid tenantId, Guid sellerId, int limit = 50, int offset = 0)
        => await context.Set<Receipt>()
            .Where(r => r.TenantId == tenantId && r.SellerId == sellerId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

    public async Task<bool> ExistsAsync(Guid tenantId, Guid? transactionId, Guid? payoutId, Guid? refundIntentId, ReceiptType type)
        => await context.Set<Receipt>()
            .AnyAsync(r => r.TenantId == tenantId
                && r.Type == type
                && (transactionId == null || r.TransactionId == transactionId)
                && (payoutId == null || r.PayoutId == payoutId)
                && (refundIntentId == null || r.RefundIntentId == refundIntentId));

    public async Task SaveChangesAsync() => await context.SaveChangesAsync();
}
