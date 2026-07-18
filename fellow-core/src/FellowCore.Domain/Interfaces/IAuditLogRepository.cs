using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IAuditLogRepository
{
    Task AddAsync(AuditLog log);
    Task<(IReadOnlyList<AuditLog> Items, int TotalCount)> ListByTenantAsync(
        Guid tenantId, string? action, int skip, int take);
}
