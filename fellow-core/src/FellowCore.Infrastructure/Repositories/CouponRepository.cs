using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class CouponRepository(AppDbContext context) : ICouponRepository
{
    public void Add(Coupon coupon) => context.Coupons.Add(coupon);
    public void Update(Coupon coupon) => context.Coupons.Update(coupon);
    public void Remove(Coupon coupon) => context.Coupons.Remove(coupon);
    public Task SaveChangesAsync() => context.SaveChangesAsync();

    public Task<Coupon?> GetByIdAsync(Guid tenantId, Guid id)
        => context.Coupons.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId);

    public Task<Coupon?> GetByCodeAsync(Guid tenantId, string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return context.Coupons.FirstOrDefaultAsync(c =>
            c.TenantId == tenantId && c.Code == normalized);
    }

    public async Task<IReadOnlyList<Coupon>> ListAsync(Guid tenantId, Guid? productId)
        => await context.Coupons
            .Where(c => c.TenantId == tenantId && c.ProductId == productId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

    public async Task<IReadOnlyList<Coupon>> ListByOwnerAsync(Guid tenantId, Guid ownerSellerId)
    {
        // Subquery: produtos donos pelo seller. Materializa em IDs pra
        // contains-check no IN(...). Single query final = 1 roundtrip ao DB.
        // Globais (ProductId IS NULL) entram pra qualquer seller do tenant —
        // assumption: produtor pode ver todos os cupons globais do próprio
        // tenant. Refinement: scope por criador (precisa OwnerSellerId no Coupon),
        // não vale agora — pra MVP single-tenant tá ok.
        var ownedProductIds = await context.Products
            .Where(p => p.TenantId == tenantId && p.OwnerSellerId == ownerSellerId)
            .Select(p => p.Id)
            .ToListAsync();

        return await context.Coupons
            .Where(c => c.TenantId == tenantId &&
                        (c.ProductId == null || ownedProductIds.Contains(c.ProductId.Value)))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
    }

    public async Task<bool> CodeExistsAsync(Guid tenantId, string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return await context.Coupons.AnyAsync(c => c.TenantId == tenantId && c.Code == normalized);
    }
}
