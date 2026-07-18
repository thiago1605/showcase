using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Interfaces;

public interface IReconciliationRepository
{
    void AddRun(ReconciliationRun run);
    void AddIssue(ReconciliationIssue issue);
    Task<ReconciliationRun?> GetLatestRunAsync(Guid tenantId, string runType);
    Task<ReconciliationIssue?> GetIssueByIdAsync(Guid tenantId, Guid issueId);
    Task<List<ReconciliationIssue>> GetOpenIssuesAsync(Guid tenantId, int limit = 100);
    Task<List<ReconciliationIssue>> GetIssuesAsync(Guid tenantId, string? resolution = null, string? severity = null, int limit = 100, int offset = 0);
    Task<List<ReconciliationRun>> GetRecentRunsAsync(Guid tenantId, int limit = 10);
    Task<int> GetOpenIssueCountAsync(Guid tenantId);
    Task SaveChangesAsync();
}
