using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Reconciliation.Interfaces;

public interface ISettlementReportProvider
{
    PaymentProvider Provider { get; }
    Task<List<SettlementReportItem>> ImportAsync(Guid reportId, string apiKey, DateTime periodStart, DateTime periodEnd, string? connectedAccountId = null, CancellationToken ct = default);
}
