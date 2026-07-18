using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IPaymentLinkRepository
{
    Task<PaymentLink?> GetByTokenAsync(string token);
    Task<PaymentLink?> GetByIdAsync(Guid tenantId, Guid id);
    Task<List<PaymentLink>> GetByTenantAsync(Guid tenantId, Guid? sellerId = null);
    void Add(PaymentLink link);
    void Update(PaymentLink link);

    /// <summary>
    /// Atomically increments UsageCount, deactivates if MaxUses reached,
    /// and creates a RESERVED PaymentLinkUsageAttempt in a single operation.
    /// Returns the attempt if successful, null if the link is exhausted/inactive.
    /// </summary>
    Task<PaymentLinkUsageAttempt?> TryReserveUsageAsync(Guid linkId);

    /// <summary>
    /// Marks the attempt as COMPLETED and stores the TransactionId.
    /// Returns true if the attempt was successfully completed, false if already finalized.
    /// </summary>
    Task<bool> CompleteUsageAttemptAsync(Guid attemptId, Guid transactionId);

    /// <summary>
    /// Marks the attempt as FAILED and atomically decrements UsageCount,
    /// re-activating the link if it was deactivated.
    /// Only rolls back if the attempt is still RESERVED (idempotent).
    /// </summary>
    Task FailUsageAttemptAsync(Guid attemptId);

    Task SaveChangesAsync();

    /// <summary>
    /// Top payment links por volume capturado em um período. Junta com
    /// PaymentLinkUsageAttempt (status COMPLETED) e Transaction. Retorna até `limit` itens.
    /// </summary>
    Task<List<(Guid LinkId, string Name, string Token, int Count, decimal Volume)>> GetTopByVolumeAsync(
        Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, int limit);
}
