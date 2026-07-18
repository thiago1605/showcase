using FellowCore.Application.Modules.Marketplace.DTOs;

namespace FellowCore.Application.Modules.Marketplace.Interfaces;

/// <summary>
/// Gestão de cupons de desconto. Producer faz CRUD; checkout público
/// consulta via MarketplaceCheckoutService (que injeta o ICouponRepository
/// pra resolver na hora da compra).
/// </summary>
public interface ICouponService
{
    Task<CouponDto> CreateAsync(Guid tenantId, Guid ownerSellerId, CreateCouponDto request);
    Task<IReadOnlyList<CouponDto>> ListAsync(Guid tenantId, Guid ownerSellerId, Guid? productId);
    /// <summary>
    /// Lista unificada pra painel /coupons no front: globais do tenant +
    /// específicos dos produtos do seller. Inclui ProductName na resposta
    /// pra UI poder exibir sem chamar /products separadamente.
    /// </summary>
    Task<IReadOnlyList<CouponDto>> ListByOwnerAsync(Guid tenantId, Guid ownerSellerId);
    Task DeleteAsync(Guid tenantId, Guid ownerSellerId, Guid couponId);

    /// <summary>
    /// Verifica + retorna desconto calculado pra um código aplicado a um produto.
    /// Endpoint público — usuário no checkout digita o código.
    /// Retorna null se inválido (não existe / expirado / esgotado / produto não bate).
    /// </summary>
    Task<CouponValidationDto?> ValidateAsync(string slug, string code);
}
