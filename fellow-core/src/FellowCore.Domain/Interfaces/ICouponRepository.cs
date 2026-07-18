using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ICouponRepository
{
    void Add(Coupon coupon);
    void Update(Coupon coupon);
    void Remove(Coupon coupon);
    Task SaveChangesAsync();

    Task<Coupon?> GetByIdAsync(Guid tenantId, Guid id);

    /// <summary>Resolução por código — case-insensitive (entity já uppercase no Create).</summary>
    Task<Coupon?> GetByCodeAsync(Guid tenantId, string code);

    /// <summary>Lista cupons. ProductId null lista os globais; non-null lista os do produto.</summary>
    Task<IReadOnlyList<Coupon>> ListAsync(Guid tenantId, Guid? productId);

    /// <summary>
    /// Lista todos os cupons "donos" pelo seller: globais do tenant +
    /// específicos dos produtos que o seller é OwnerSeller. Pra painel
    /// unificado em /coupons no front (produtor vê tudo num lugar só).
    /// </summary>
    Task<IReadOnlyList<Coupon>> ListByOwnerAsync(Guid tenantId, Guid ownerSellerId);

    Task<bool> CodeExistsAsync(Guid tenantId, string code);
}
