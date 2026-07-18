using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ISplitAllocationRepository
{
    Task<List<SplitAllocation>> GetByTransactionIdAsync(Guid tenantId, Guid transactionId);
    Task AddRangeAsync(IEnumerable<SplitAllocation> allocations);
    Task SaveChangesAsync();
}
