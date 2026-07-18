using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ISplitTransferRepository
{
    void Add(SplitTransfer transfer);
    void Update(SplitTransfer transfer);
    Task<SplitTransfer?> GetByIdAsync(Guid tenantId, Guid id);
    Task<SplitTransfer?> GetByTransactionAndRecipientAsync(Guid tenantId, Guid transactionId, Guid recipientSellerId, bool isPrimaryShare = false);
    Task<IReadOnlyList<SplitTransfer>> GetByTransactionIdAsync(Guid tenantId, Guid transactionId);
    Task<IReadOnlyList<SplitTransfer>> GetPendingByTransactionIdAsync(Guid transactionId);
    Task<(int Pending, int Failed)> GetStatusCountsAsync(Guid tenantId);
    Task SaveChangesAsync();
}
