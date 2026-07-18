using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IRefundIntentRepository
{
    Task<List<RefundIntent>> GetByTransactionIdAsync(Guid transactionId);
    Task<RefundIntent?> GetByIdempotencyKeyAsync(Guid tenantId, string idempotencyKey);
    Task<RefundIntent?> GetByIdAsync(Guid id);
    Task<List<RefundIntent>> GetRetryDueAsync(DateTime utcNow, int limit = 50);
    Task<int> GetStuckProcessingCountAsync(TimeSpan olderThan);
    Task<List<RefundIntent>> GetCompletedByTenantAsync(Guid tenantId, DateTime since);
    /// <summary>
    /// Lists refunds for the given tenant. When sellerId is non-null, joins on Transaction
    /// to filter to that seller only — used by the seller portal at GET /api/v1/refunds.
    /// </summary>
    Task<(List<RefundIntent> Items, int Total)> ListAsync(
        Guid tenantId,
        Guid? sellerId,
        Domain.Enums.RefundIntentStatus? status,
        DateTime? from,
        DateTime? to,
        int page,
        int pageSize);
    void Add(RefundIntent refundIntent);
    void Update(RefundIntent refundIntent);
    Task SaveChangesAsync();
}
