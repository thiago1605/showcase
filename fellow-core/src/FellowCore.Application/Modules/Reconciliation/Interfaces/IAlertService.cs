using FellowCore.Domain.Entities;

namespace FellowCore.Application.Modules.Reconciliation.Interfaces;

public interface IAlertService
{
    Task SendCriticalAlertAsync(ReconciliationIssue issue, CancellationToken ct = default);
    Task SendWarningDigestAsync(IReadOnlyList<ReconciliationIssue> issues, CancellationToken ct = default);
}
