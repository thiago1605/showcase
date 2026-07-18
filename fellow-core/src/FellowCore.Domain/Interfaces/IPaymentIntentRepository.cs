using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IPaymentIntentRepository
{
    Task<PaymentIntent?> GetByExternalReferenceAsync(Guid tenantId, string externalReferenceId);
    /// <summary>
    /// Loads a PaymentIntent by ID without a tenant filter. Used by callers that do not have
    /// a tenant context (e.g. internal dispute handler which already holds the transaction).
    /// Prefer the tenant-scoped overload for any user-facing or externally-triggered code path.
    /// </summary>
    Task<PaymentIntent?> GetByIdAsync(Guid id);
    /// <summary>
    /// Loads a PaymentIntent scoped to a specific tenant, preventing cross-tenant access.
    /// </summary>
    Task<PaymentIntent?> GetByIdAsync(Guid tenantId, Guid id);
    void Add(PaymentIntent intent);
    void Update(PaymentIntent intent);
    Task SaveChangesAsync();

    /// <summary>
    /// Atomically try to set CapturedTransactionId. Returns true if this call won the race.
    /// Uses optimistic concurrency (RowVersion) to guarantee exactly-once capture.
    /// </summary>
    Task<bool> TryCaptureAsync(Guid intentId, Guid transactionId);
}
