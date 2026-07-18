using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ISellerRepository
{
    Task<bool> ExistsByDocumentAsync(Guid tenantId, string document);
    Task<Seller?> GetByIdAsync(Guid tenantId, Guid sellerId);
    Task<IReadOnlyList<Seller>> GetByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> sellerIds);
    void Add(Seller seller);
    void Update(Seller seller);
    Task<IEnumerable<Seller>> GetAllAsync(Guid tenantId);
    Task<(IReadOnlyList<Seller> Items, int TotalCount)> GetPagedAsync(Guid tenantId, int skip, int take);
    /// <summary>
    /// No TenantId filter — called from webhooks where TenantId is unknown upfront.
    /// ACCEPTED RISK: External account IDs (Stripe acct_*, OpenPix correlationId) are globally unique
    /// by design and cannot collide across tenants.
    /// </summary>
    Task<Seller?> GetByExternalAccountIdAsync(string externalAccountId);

    Task SaveChangesAsync();

    /// <summary>
    /// True se já existe um seller deste tenant marcado como Founding com o número informado.
    /// Usado pela operação admin <c>SetFoundingAsync</c> pra dar erro 409 amigável antes de
    /// bater no unique index do banco (mensagem clara > stack trace de DbUpdateException).
    /// </summary>
    Task<bool> IsFoundingNumberTakenAsync(Guid tenantId, int foundingNumber, Guid? excludingSellerId = null);

    /// <summary>
    /// Soma de <c>NetAmount</c> das TXs CAPTURED do seller na janela [<paramref name="since"/>, <c>now</c>].
    /// Usado pelo cálculo de tier (TPV30d/TPV90d) e por relatórios. Retorna 0 quando não há TXs.
    /// </summary>
    Task<decimal> GetCapturedNetSumAsync(Guid tenantId, Guid sellerId, DateTime since);

    /// <summary>
    /// Listagem leve (TenantId, SellerId) dos sellers ACTIVE — pro job mensal de
    /// recalculo de tier iterar sem carregar o aggregate inteiro. Single-shot
    /// (sem pagination), limitado pelo batchSize. Quando escala exigir, mover pra
    /// stream/cursor — por ora 5000 cobre largura de tenants do MVP.
    /// </summary>
    Task<List<(Guid TenantId, Guid SellerId)>> GetActiveTenantSellerPairsAsync(int batchSize = 5000);
}