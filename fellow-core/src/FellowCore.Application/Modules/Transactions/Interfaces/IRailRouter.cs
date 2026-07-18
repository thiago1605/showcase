using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Transactions.Interfaces;

public interface IRailRouter
{
    IPaymentRail ResolveRail(PaymentType method, Tenant tenant, Seller? seller);

    /// <summary>
    /// Resolve rail for an already-persisted transaction (webhook path).
    /// Uses transaction.PaymentType and transaction.Provider to find the correct rail.
    /// </summary>
    IPaymentRail ResolveRailForTransaction(Transaction transaction);

    /// <summary>
    /// Returns the next-priority failover rail for the given payment type,
    /// excluding the failed rail. Returns null if no failover is available.
    /// </summary>
    IPaymentRail? ResolveFailoverRail(PaymentType method, PaymentRailType failedRail);
}
