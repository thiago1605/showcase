using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class TenantRepository(AppDbContext _context) : ITenantRepository
{
    async Task<Tenant> ITenantRepository.AddAsync(Tenant tenant)
    {
        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync();
        return tenant; 
    }

    async Task<Tenant?> ITenantRepository.GetBySlugAsync(string slug)
    {
        return await _context.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);
    }

    public async Task<Tenant?> GetByApiKeyHashAsync(string apiKeyHash)
    {
        return await _context.Tenants.FirstOrDefaultAsync(t => t.ApiKeyHash == apiKeyHash && !t.IsDeleted);
    }

    public async Task<Tenant?> GetByIdWithConfigAsync(Guid tenantId)
    {
        return await _context.Tenants.Include(tenant => tenant.Config).FirstOrDefaultAsync(tenant => tenant.Id == tenantId);
    }

    public async Task<List<Tenant>> GetAllAsync()
    {
        return await _context.Tenants.Where(t => !t.IsDeleted).ToListAsync();
    }

    public async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
    }
}