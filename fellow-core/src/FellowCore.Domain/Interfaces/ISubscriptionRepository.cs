using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Interfaces;

public interface ISubscriptionRepository
{
    Task<Subscription?> GetByIdAsync(Guid tenantId, Guid id);
    Task<(IReadOnlyList<Subscription> Items, int TotalCount)> GetPagedAsync(
        Guid tenantId, int skip, int take, Guid? sellerId = null, SubscriptionStatus? status = null);
    Task<IReadOnlyList<Subscription>> GetDueForBillingAsync(DateTime referenceDate, int batchSize = 100);
    void Add(Subscription subscription);
    void Update(Subscription subscription);
    Task SaveChangesAsync();
}
