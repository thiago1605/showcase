using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ITransactionItemRepository
{
    Task<List<TransactionItem>> GetByTransactionIdAsync(Guid tenantId, Guid transactionId);
    Task AddRangeAsync(IEnumerable<TransactionItem> items);
}
