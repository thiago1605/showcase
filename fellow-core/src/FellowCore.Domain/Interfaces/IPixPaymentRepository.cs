using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IPixPaymentRepository
{
    void Add(PixPayment payment);
    void Update(PixPayment payment);
    Task<PixPayment?> GetByIdAsync(Guid tenantId, Guid id);
    Task<PixPayment?> GetByCorrelationIdAsync(Guid tenantId, string correlationId);
    Task<(IEnumerable<PixPayment> Items, int TotalCount)> GetPagedAsync(Guid tenantId, int skip, int take);
    Task SaveChangesAsync();
}
