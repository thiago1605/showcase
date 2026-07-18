using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class ProductOrderBumpRepository(AppDbContext context) : IProductOrderBumpRepository
{
    public void Add(ProductOrderBump bump) => context.ProductOrderBumps.Add(bump);
    public void Update(ProductOrderBump bump) => context.ProductOrderBumps.Update(bump);
    public void Remove(ProductOrderBump bump) => context.ProductOrderBumps.Remove(bump);
    public Task SaveChangesAsync() => context.SaveChangesAsync();

    public async Task<ProductOrderBump?> GetByIdAsync(Guid tenantId, Guid bumpId)
        => await context.ProductOrderBumps
            .FirstOrDefaultAsync(b => b.Id == bumpId && b.TenantId == tenantId);

    public async Task<IReadOnlyList<ProductOrderBump>> ListByMainProductAsync(Guid tenantId, Guid mainProductId)
        => await context.ProductOrderBumps
            .Include(b => b.BumpProduct)
            .Where(b => b.TenantId == tenantId && b.MainProductId == mainProductId)
            .OrderBy(b => b.DisplayOrder)
            .ThenBy(b => b.CreatedAt)
            .ToListAsync();

    public async Task<IReadOnlyList<ProductOrderBump>> ListActiveByMainProductAsync(Guid tenantId, Guid mainProductId)
        => await context.ProductOrderBumps
            .Include(b => b.BumpProduct)
            .Where(b =>
                b.TenantId == tenantId &&
                b.MainProductId == mainProductId &&
                b.IsActive)
            .OrderBy(b => b.DisplayOrder)
            .ThenBy(b => b.CreatedAt)
            .ToListAsync();

    public async Task<int> CountActiveByMainProductAsync(Guid tenantId, Guid mainProductId)
        => await context.ProductOrderBumps
            .CountAsync(b =>
                b.TenantId == tenantId &&
                b.MainProductId == mainProductId &&
                b.IsActive);

    public async Task<bool> ExistsActivePairAsync(Guid tenantId, Guid mainProductId, Guid bumpProductId)
        => await context.ProductOrderBumps
            .AnyAsync(b =>
                b.TenantId == tenantId &&
                b.MainProductId == mainProductId &&
                b.BumpProductId == bumpProductId &&
                b.IsActive);
}
