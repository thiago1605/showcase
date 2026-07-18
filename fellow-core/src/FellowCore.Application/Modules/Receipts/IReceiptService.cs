using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Receipts;

public interface IReceiptService
{
    Task<Receipt> GenerateForPaymentAsync(Guid tenantId, Guid transactionId);
    Task<Receipt> GenerateForRefundAsync(Guid tenantId, Guid refundIntentId);
    Task<Receipt> GenerateForPayoutAsync(Guid tenantId, Guid payoutId);
    Task<Receipt> GenerateForSplitReceivedAsync(Guid tenantId, Guid transactionId, Guid recipientSellerId, decimal amount);
    Task<Receipt?> GetByIdAsync(Guid tenantId, Guid receiptId);
    Task<List<Receipt>> GetBySellerAsync(Guid tenantId, Guid sellerId, int limit = 50, int offset = 0);
}
