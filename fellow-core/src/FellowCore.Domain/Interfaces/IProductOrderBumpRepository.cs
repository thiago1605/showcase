using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

/// <summary>
/// Persistência de order bumps (ofertas adicionais no checkout do produto principal).
/// Tenant-scoped em todas as queries pra evitar leakage cross-tenant.
/// </summary>
public interface IProductOrderBumpRepository
{
    void Add(ProductOrderBump bump);
    void Update(ProductOrderBump bump);
    void Remove(ProductOrderBump bump);
    Task SaveChangesAsync();

    Task<ProductOrderBump?> GetByIdAsync(Guid tenantId, Guid bumpId);

    /// <summary>
    /// Lista os bumps de um main product (ativos e inativos), ordenados por
    /// DisplayOrder. Usado pelo painel do produtor. Inclui BumpProduct (Include)
    /// pra o caller exibir nome/preço/cover sem N+1.
    /// </summary>
    Task<IReadOnlyList<ProductOrderBump>> ListByMainProductAsync(Guid tenantId, Guid mainProductId);

    /// <summary>
    /// Lista somente os bumps ATIVOS de um main product, ordenados por DisplayOrder.
    /// Inclui BumpProduct (somente PUBLISHED — não-publicado é filtrado fora
    /// pelo service). Usado pelo checkout público.
    /// </summary>
    Task<IReadOnlyList<ProductOrderBump>> ListActiveByMainProductAsync(Guid tenantId, Guid mainProductId);

    /// <summary>
    /// Conta bumps ATIVOS de um main product — usado pra validar o limite de 3 ativos.
    /// </summary>
    Task<int> CountActiveByMainProductAsync(Guid tenantId, Guid mainProductId);

    /// <summary>
    /// Conta bumps ativos pra um par (mainProduct, bumpProduct) — dedup helper:
    /// não permite o mesmo produto ofertado 2x como bump no mesmo main.
    /// </summary>
    Task<bool> ExistsActivePairAsync(Guid tenantId, Guid mainProductId, Guid bumpProductId);
}
