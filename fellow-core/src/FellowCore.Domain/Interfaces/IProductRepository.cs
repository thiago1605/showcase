using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Interfaces;

/// <summary>
/// Snapshot agregado de métricas do produtor — usado pelo card "resumo" do
/// painel de produtos. Janela móvel de 30 dias pra Sales/Volume; contagens
/// de produtos são all-time.
/// </summary>
public record ProductOwnerStats(
    int TotalProducts,
    int PublishedCount,
    int DraftCount,
    int PausedCount,
    // Janela configurável (7/30/90 dias)
    int PeriodDays,
    int SalesInPeriod,
    decimal VolumeInPeriod,
    // Período anterior (mesma duração imediatamente antes) — pra badges de variação.
    int PreviousSalesInPeriod,
    decimal PreviousVolumeInPeriod,
    // Timeseries — arrays de `PeriodDays` ints (índice 0 = (days-1) dias atrás).
    int[] SalesByDay,
    decimal[] VolumeByDay,
    // Comissões pagas pra afiliados + co-producers (não inclui o próprio
    // produtor). Líquido de reversões. PAID only — pending/scheduled fica fora
    // porque ainda não saiu do clearing.
    decimal CommissionsPaidInPeriod,
    decimal PreviousCommissionsPaidInPeriod
);

/// <summary>
/// Métricas por produto pra render inline na listagem. Sales/Volume janela
/// 30d (mesma do owner stats — consistência mental); ActiveAffiliates é
/// estado atual (Status=APPROVED), não histórico.
/// </summary>
public record ProductMetricsSnapshot(
    Guid ProductId,
    int Sales30d,
    decimal Volume30d,
    int ActiveAffiliates,
    // Timeseries 30 dias da venda — pra sparkline na row de cada produto.
    int[] SalesByDay
);

public interface IProductRepository
{
    void Add(Product product);
    void Update(Product product);
    Task SaveChangesAsync();

    Task<Product?> GetByIdAsync(Guid tenantId, Guid productId);

    /// <summary>
    /// Fetch em batch — útil pra evitar N+1 quando precisamos resolver vários
    /// produtos de uma vez (ex.: ranking "Top produtos vendidos" do dashboard).
    /// Retorna apenas os IDs que existem no tenant; IDs ausentes/cross-tenant
    /// são silenciosamente filtrados.
    /// </summary>
    Task<IReadOnlyList<Product>> GetByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> productIds);

    /// <summary>Resolução por slug + tenant — usado em URL pública /p/{slug}.</summary>
    Task<Product?> GetBySlugAsync(Guid tenantId, string slug);

    /// <summary>Lista paginada de produtos do produtor logado (filtros opcionais).</summary>
    Task<(IReadOnlyList<Product> Items, int TotalCount)> GetByOwnerAsync(
        Guid tenantId,
        Guid ownerSellerId,
        ProductStatus? status,
        int skip,
        int take);

    /// <summary>
    /// Catálogo de afiliação — produtos PUBLISHED + AffiliationMode != CLOSED.
    /// Sellers podem afiliar a produtos de qualquer outro seller do mesmo
    /// tenant, mas nunca aos próprios produtos. Quando informado,
    /// <paramref name="excludeOwnerSellerId"/> remove os produtos cujo owner
    /// seja o próprio caller. Filtros opcionais por categoria(s), ticket etc.
    /// <paramref name="categories"/> é multi-valor: passe uma lista para
    /// fazer OR (any of); null/empty desabilita o filtro de categoria.
    /// </summary>
    Task<(IReadOnlyList<Product> Items, int TotalCount)> GetMarketplaceCatalogAsync(
        Guid tenantId,
        IReadOnlyList<string>? categories,
        decimal? minPrice,
        decimal? maxPrice,
        AffiliationMode? mode,
        int skip,
        int take,
        Guid? excludeOwnerSellerId = null);

    /// <summary>
    /// Universo de categorias disponíveis no catálogo de afiliação — todas as
    /// categorias distintas de produtos PUBLISHED + afiliação aberta no tenant,
    /// com contagem. Ignora o filtro de categoria do caller (para alimentar o
    /// chip rail multi-select). Demais filtros podem ser aplicados se passados.
    /// </summary>
    Task<IReadOnlyList<(string Name, int Count)>> GetMarketplaceCategoriesAsync(
        Guid tenantId,
        Guid? excludeOwnerSellerId = null);

    /// <summary>Verifica se o slug já existe (pra validação na criação).</summary>
    Task<bool> SlugExistsAsync(Guid tenantId, string slug);

    /// <summary>
    /// Resolução pública de produto por slug SEM tenant — usado pelo checkout
    /// público (`/p/{slug}`) que não tem auth e portanto não conhece o tenant
    /// até resolver o produto. Retorna apenas produtos PUBLISHED. Slug é
    /// efetivamente único globalmente no MVP single-tenant; multi-tenant real
    /// requer disambiguação via host/subdomínio.
    /// </summary>
    Task<Product?> GetPublishedBySlugGlobalAsync(string slug);

    /// <summary>
    /// Agregação para o painel "Meus produtos": contagens por status (all-time)
    /// + sales/volume + commissions na janela `days`. Filtra TX captured +
    /// ExternalReferenceId LIKE 'product:%' do produtor logado.
    /// </summary>
    Task<ProductOwnerStats> GetOwnerStatsAsync(Guid tenantId, Guid ownerSellerId, int days);

    /// <summary>
    /// Métricas por produto (sales 30d, volume 30d, # afiliados ativos). Recebe
    /// um set de productIds (página atual da listagem) e retorna dicionário
    /// somente com produtos que TÊM atividade — chamador trata ausência como zero.
    ///
    /// Estratégia: duas queries em paralelo (TX agregado + Afiliação agregado).
    /// Mantemos N+1 longe usando GROUP BY na DB — uma query por bloco, não por
    /// produto. Em produtor com 20 produtos por página, é 2 queries, não 40.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ProductMetricsSnapshot>> GetMetricsForProductsAsync(
        Guid tenantId, IReadOnlyList<Guid> productIds);

    // === Assets / materiais de divulgação ===
    void AddAsset(ProductAsset asset);
    Task<ProductAsset?> GetAssetByIdAsync(Guid tenantId, Guid assetId);
    Task<IReadOnlyList<ProductAsset>> ListAssetsAsync(Guid tenantId, Guid productId);
    void RemoveAsset(ProductAsset asset);
}
