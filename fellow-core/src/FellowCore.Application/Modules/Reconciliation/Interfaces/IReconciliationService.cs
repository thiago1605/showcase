using FellowCore.Domain.Entities;

namespace FellowCore.Application.Modules.Reconciliation.Interfaces;

public interface IReconciliationService
{
    /// <summary>Batch reconciliation — runs all phases for all tenants.</summary>
    Task RunDailyReconciliationAsync(CancellationToken ct = default);

    /// <summary>Event-driven reconciliation — validates a single transaction against Stripe.</summary>
    Task ReconcileTransactionAsync(Guid tenantId, Guid transactionId, CancellationToken ct = default);

    /// <summary>Event-driven reconciliation — validates a single payout against ledger.</summary>
    Task ReconcilePayoutAsync(Guid tenantId, Guid payoutId, CancellationToken ct = default);

    /// <summary>
    /// Applies actual provider cost from settlement data and adjusts the ledger if different from estimated.
    /// Called by settlement import jobs after receiving actual fee data from Stripe/OpenPix.
    /// </summary>
    Task ApplyActualProviderCostAsync(Guid tenantId, Guid transactionId, decimal actualCost, CancellationToken ct = default);
}
