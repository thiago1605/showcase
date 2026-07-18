using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ISellerRiskProfileRepository
{
    Task<SellerRiskProfile?> GetBySellerIdAsync(Guid sellerId);
    Task<List<Guid>> GetActiveSellerIdsAsync(int batchSize = 500);
    void Add(SellerRiskProfile profile);
    void Update(SellerRiskProfile profile);
    Task SaveChangesAsync();
}
