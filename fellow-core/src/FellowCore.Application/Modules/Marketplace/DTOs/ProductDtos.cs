using System.ComponentModel.DataAnnotations;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Marketplace.DTOs;

public record CreateProductDto(
    [Required, MaxLength(200)] string Name,
    [Range(0.01, double.MaxValue)] decimal Price,
    [Required] ProductType Type,
    [Range(0, 100)] decimal DefaultAffiliateCommissionPercent,
    [Required] AffiliationMode AffiliationMode,
    [MaxLength(5000)] string? Description = null,
    [MaxLength(500)] string? CoverImageUrl = null,
    [MaxLength(1000)] string? DeliveryUrl = null,
    Guid? SplitRuleId = null,
    [MaxLength(50)] string? Category = null,
    /// <summary>Slug opcional. Se null, gera do Name. Se fornecido,
    /// valida unicidade e formato (URL-safe).</summary>
    [MaxLength(100)] string? Slug = null
);

public record UpdateProductDto(
    [MaxLength(200)] string? Name = null,
    [MaxLength(5000)] string? Description = null,
    [MaxLength(500)] string? CoverImageUrl = null,
    [Range(0.01, double.MaxValue)] decimal? Price = null,
    [MaxLength(1000)] string? DeliveryUrl = null,
    [Range(0, 100)] decimal? DefaultAffiliateCommissionPercent = null,
    AffiliationMode? AffiliationMode = null,
    Guid? SplitRuleId = null,
    [MaxLength(50)] string? Category = null,
    /// <summary>Facebook Pixel ID — string vazia remove o tracking; null mantém.</summary>
    [MaxLength(50)] string? FacebookPixelId = null,
    /// <summary>Google Ads conversion ID/label — formato "AW-XXX/YYY".</summary>
    [MaxLength(100)] string? GoogleAdsConversionId = null
);

public record ProductDto(
    Guid Id,
    Guid OwnerSellerId,
    string? OwnerSellerName,
    string Name,
    string Slug,
    string? Description,
    string? CoverImageUrl,
    decimal Price,
    string Currency,
    int Type,
    string? DeliveryUrl,
    decimal DefaultAffiliateCommissionPercent,
    int AffiliationMode,
    int Status,
    Guid? SplitRuleId,
    string? Category,
    string? FacebookPixelId,
    string? GoogleAdsConversionId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // Métricas por produto — só preenchidas no path /products (listagem do
    // produtor). GetById, catálogo de afiliação e checkout público deixam null
    // para evitar query extra desnecessária. Frontend renderiza só quando != null.
    ProductMetricsDto? Metrics = null,
    /// <summary>
    /// Status da afiliação existente do caller para este produto. Preenchido
    /// apenas no catálogo de afiliação (/marketplace/products); null nos demais
    /// paths. Quando null, o seller ainda não interagiu com o produto.
    /// Valores: 0=PENDING, 1=APPROVED, 2=REJECTED, 3=REVOKED.
    /// </summary>
    int? CurrentSellerAffiliationStatus = null
);

public record ProductListDto(
    IReadOnlyList<ProductDto> Items,
    int TotalCount,
    /// <summary>
    /// Universo de categorias disponíveis no catálogo (independente dos filtros
    /// de categoria do caller — outros filtros como mode/excludeOwner aplicam).
    /// Permite que o frontend renderize o chip rail estável (chips não somem ao
    /// selecionar). Vazio nos endpoints fora do marketplace catalog.
    /// </summary>
    IReadOnlyList<CategoryFacetDto>? AvailableCategories = null
);

/// <summary>
/// Faceta de categoria — nome canônico + contagem de produtos PUBLISHED do
/// tenant. Usada para popular chips de filtro no catálogo de afiliação.
/// </summary>
public record CategoryFacetDto(string Name, int Count);

/// <summary>
/// Resumo agregado pro topo do painel "Meus produtos". Total/Published/Draft/
/// Paused são all-time; Sales30d e Volume30d janela móvel de 30 dias.
/// </summary>
public record ProductStatsDto(
    int TotalProducts,
    int PublishedCount,
    int DraftCount,
    int PausedCount,
    int PeriodDays,
    int SalesInPeriod,
    decimal VolumeInPeriod,
    int PreviousSalesInPeriod,
    decimal PreviousVolumeInPeriod,
    int[] SalesByDay,
    decimal[] VolumeByDay,
    decimal CommissionsPaidInPeriod,
    decimal PreviousCommissionsPaidInPeriod
);

/// <summary>
/// Métricas por produto pra render inline na listagem. ActiveAffiliates é
/// estado atual (Status=APPROVED), o resto é janela 30d.
/// </summary>
public record ProductMetricsDto(
    int Sales30d,
    decimal Volume30d,
    int ActiveAffiliates,
    /// <summary>Vendas por dia nos últimos 30d — pra sparkline na row do produto.</summary>
    int[] SalesByDay
);

/// <summary>
/// Material de divulgação anexado ao produto. Produtor faz CRUD; afiliado
/// aprovado lista pra baixar.
/// </summary>
public record ProductAssetDto(
    Guid Id,
    Guid ProductId,
    string Title,
    string Type,
    string Url,
    string? MimeType,
    long? SizeBytes,
    DateTime CreatedAt
);

public record CreateProductAssetDto(
    [Required, MaxLength(200)] string Title,
    [Required, MaxLength(50)] string Type,
    [Required, MaxLength(1000)] string Url,
    [MaxLength(100)] string? MimeType = null,
    long? SizeBytes = null
);
