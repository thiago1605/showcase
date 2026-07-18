using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Marketplace.Interfaces;

public interface IProductService
{
    Task<ProductDto> CreateAsync(Guid tenantId, Guid ownerSellerId, CreateProductDto request);

    Task<ProductDto> UpdateAsync(Guid tenantId, Guid ownerSellerId, Guid productId, UpdateProductDto request);

    Task<ProductDto?> GetByIdAsync(Guid tenantId, Guid productId);
    Task<ProductDto?> GetBySlugAsync(Guid tenantId, string slug);

    Task<ProductListDto> ListByOwnerAsync(
        Guid tenantId,
        Guid ownerSellerId,
        ProductStatus? status,
        int page,
        int pageSize);

    Task<ProductListDto> ListMarketplaceCatalogAsync(
        Guid tenantId,
        IReadOnlyList<string>? categories,
        decimal? minPrice,
        decimal? maxPrice,
        AffiliationMode? mode,
        int page,
        int pageSize,
        Guid? excludeOwnerSellerId = null);

    Task<ProductDto> PublishAsync(Guid tenantId, Guid ownerSellerId, Guid productId);
    Task<ProductDto> PauseAsync(Guid tenantId, Guid ownerSellerId, Guid productId);
    Task<ProductDto> ResumeAsync(Guid tenantId, Guid ownerSellerId, Guid productId);
    Task<ProductDto> ArchiveAsync(Guid tenantId, Guid ownerSellerId, Guid productId);

    /// <summary>
    /// Resumo agregado do painel "Meus produtos": contagens por status (all-time)
    /// + sales/volume da janela 30d.
    /// </summary>
    Task<ProductStatsDto> GetOwnerStatsAsync(Guid tenantId, Guid ownerSellerId, int days = 30);

    // === Assets / materiais de divulgação ===

    /// <summary>
    /// Cria um asset (material de divulgação) num produto. Requer ownership —
    /// só o produtor dono do produto pode adicionar.
    /// </summary>
    Task<ProductAssetDto> AddAssetAsync(Guid tenantId, Guid ownerSellerId, Guid productId, CreateProductAssetDto request);

    /// <summary>Lista assets de um produto.</summary>
    Task<IReadOnlyList<ProductAssetDto>> ListAssetsAsync(Guid tenantId, Guid productId);

    /// <summary>Remove um asset. Requer ownership do produto.</summary>
    Task DeleteAssetAsync(Guid tenantId, Guid ownerSellerId, Guid assetId);
}
