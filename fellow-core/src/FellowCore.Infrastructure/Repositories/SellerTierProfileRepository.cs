using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class SellerTierProfileRepository(AppDbContext _context) : ISellerTierProfileRepository
{
    public Task<SellerTierProfile?> GetBySellerIdAsync(Guid tenantId, Guid sellerId)
    {
        return _context.SellerTierProfiles
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.SellerId == sellerId);
    }

    public Task<List<SellerTierProfile>> GetBatchAsync(int skip, int take)
    {
        if (take <= 0 || take > 5000)
            throw new ArgumentOutOfRangeException(nameof(take), "take precisa estar entre 1 e 5000.");

        return _context.SellerTierProfiles
            .OrderBy(p => p.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
    }

    public void Add(SellerTierProfile profile) => _context.SellerTierProfiles.Add(profile);

    public void Update(SellerTierProfile profile) => _context.SellerTierProfiles.Update(profile);

    public Task SaveChangesAsync() => _context.SaveChangesAsync();
}
