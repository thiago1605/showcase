using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Interfaces;

public interface IReceiptRepository
{
    void Add(Receipt receipt);
    Task<Receipt?> GetByIdAsync(Guid tenantId, Guid id);
    Task<Receipt?> GetByTransactionIdAsync(Guid tenantId, Guid transactionId, ReceiptType type);
    Task<Receipt?> GetByPayoutIdAsync(Guid tenantId, Guid payoutId);
    Task<Receipt?> GetByRefundIntentIdAsync(Guid tenantId, Guid refundIntentId);
    Task<Receipt?> GetBySplitReceivedAsync(Guid tenantId, Guid transactionId, Guid sellerId);
    Task<List<Receipt>> GetBySellerAsync(Guid tenantId, Guid sellerId, int limit = 50, int offset = 0);
    Task<bool> ExistsAsync(Guid tenantId, Guid? transactionId, Guid? payoutId, Guid? refundIntentId, ReceiptType type);
    Task SaveChangesAsync();
}
