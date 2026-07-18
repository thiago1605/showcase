using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ISellerTierProfileRepository
{
    /// <summary>
    /// Profile persistido do seller (null = nunca foi calculado pelo job —
    /// caller deve cair pro fallback on-the-fly).
    /// </summary>
    Task<SellerTierProfile?> GetBySellerIdAsync(Guid tenantId, Guid sellerId);

    /// <summary>
    /// Stream/paginação dos profiles existentes — usado pelo job mensal e por
    /// relatórios admin. Lê em batches pra não inflar memória.
    /// </summary>
    Task<List<SellerTierProfile>> GetBatchAsync(int skip, int take);

    void Add(SellerTierProfile profile);
    void Update(SellerTierProfile profile);
    Task SaveChangesAsync();
}
