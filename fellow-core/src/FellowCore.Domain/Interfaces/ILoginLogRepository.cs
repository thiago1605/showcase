using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ILoginLogRepository
{
    void Add(LoginLog log);
    Task<List<LoginLog>> GetByUserAsync(Guid userId, int limit = 50, DateTime? from = null, DateTime? to = null);
    Task<List<LoginLog>> GetByTenantAsync(Guid tenantId, int page, int pageSize);
    Task<int> CountByTenantAsync(Guid tenantId);
    Task SaveChangesAsync();
}
