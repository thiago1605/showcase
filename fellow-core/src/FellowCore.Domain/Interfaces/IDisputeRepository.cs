using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Interfaces;

public interface IDisputeRepository
{
    /// <summary>
    /// No TenantId filter — called from webhook handlers where TenantId is resolved from the
    /// linked Transaction. ACCEPTED RISK: Stripe dispute IDs (dp_*) are globally unique by design.
    /// </summary>
    Task<Dispute?> GetByExternalIdAsync(string externalDisputeId);
    Task<Dispute?> GetByTransactionIdAsync(Guid transactionId);
    Task<List<Dispute>> GetByTenantAsync(Guid tenantId, DisputeStatus? status = null, int skip = 0, int take = 50);
    Task<(int Count, decimal ExposureAmount)> GetOpenDisputeSummaryAsync(Guid tenantId);
    void Add(Dispute dispute);
    void Update(Dispute dispute);
    Task SaveChangesAsync();
}
