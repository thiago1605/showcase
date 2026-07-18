using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class SellerRiskProfileRepository(AppDbContext _context) : ISellerRiskProfileRepository
{
    public async Task<SellerRiskProfile?> GetBySellerIdAsync(Guid sellerId)
        => await _context.Set<SellerRiskProfile>()
            .FirstOrDefaultAsync(p => p.SellerId == sellerId);

    public async Task<List<Guid>> GetActiveSellerIdsAsync(int batchSize = 500)
        => await _context.Sellers
            .AsNoTracking()
            .Where(s => s.Status == SellerStatus.ACTIVE)
            .OrderBy(s => s.CreatedAt) // FIFO determinístico, evita drift entre rodadas
            .Take(batchSize)
            .Select(s => s.Id)
            .ToListAsync();

    public void Add(SellerRiskProfile profile) => _context.Set<SellerRiskProfile>().Add(profile);
    public void Update(SellerRiskProfile profile) => _context.Set<SellerRiskProfile>().Update(profile);
    public async Task SaveChangesAsync() => await _context.SaveChangesAsync();
}
