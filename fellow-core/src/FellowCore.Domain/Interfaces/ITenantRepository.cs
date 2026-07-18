using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;


public interface ITenantRepository
{
    Task<Tenant?> GetBySlugAsync(string slug);
    Task<Tenant> AddAsync(Tenant tenant);
    Task<Tenant?> GetByApiKeyHashAsync(string apiKeyHash);
    Task<Tenant?> GetByIdWithConfigAsync(Guid tenantId);
    Task<List<Tenant>> GetAllAsync();
    Task SaveChangesAsync();
}