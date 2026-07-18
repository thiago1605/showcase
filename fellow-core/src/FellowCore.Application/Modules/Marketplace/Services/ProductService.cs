using System.Globalization;
using System.Text;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.Marketplace.Services;

public class ProductService(
    IProductRepository productRepository,
    ISellerRepository sellerRepository,
    IAffiliationRepository affiliationRepository
) : IProductService
{
    public async Task<ProductDto> CreateAsync(Guid tenantId, Guid ownerSellerId, CreateProductDto request)
    {
        // Slug: usa o fornecido OU gera do nome. Se gerado e já existir, anexa
        // sufixo numérico até ser único (max 5 tentativas — produtor com 5
        // produtos de nome igual deve renomear).
        var slug = string.IsNullOrWhiteSpace(request.Slug)
            ? GenerateSlug(request.Name)
            : NormalizeSlug(request.Slug);

        if (string.IsNullOrWhiteSpace(slug))
            throw new BusinessException("Product.InvalidSlug",
                "Não foi possível gerar um slug a partir do nome. Forneça um slug manual.");

        var finalSlug = slug;
        for (int i = 1; i <= 5 && await productRepository.SlugExistsAsync(tenantId, finalSlug); i++)
            finalSlug = $"{slug}-{i}";

        if (await productRepository.SlugExistsAsync(tenantId, finalSlug))
            throw new BusinessException("Product.SlugTaken",
                "O slug informado já está em uso. Escolha outro.");

        var product = Product.Create(
            tenantId: tenantId,
            ownerSellerId: ownerSellerId,
            name: request.Name,
            slug: finalSlug,
            price: request.Price,
            type: request.Type,
            defaultAffiliateCommissionPercent: request.DefaultAffiliateCommissionPercent,
            affiliationMode: request.AffiliationMode,
            description: request.Description,
            coverImageUrl: request.CoverImageUrl,
            deliveryUrl: request.DeliveryUrl,
            splitRuleId: request.SplitRuleId,
            category: request.Category);

        productRepository.Add(product);
        await productRepository.SaveChangesAsync();
        return await ToDtoAsync(product);
    }

    public async Task<ProductDto> UpdateAsync(Guid tenantId, Guid ownerSellerId, Guid productId, UpdateProductDto request)
    {
        var product = await productRepository.GetByIdAsync(tenantId, productId)
            ?? throw new NotFoundException("Product.NotFound", $"Produto {productId} não encontrado.");
        AssertOwnership(product, ownerSellerId);

        product.Update(
            name: request.Name,
            description: request.Description,
            coverImageUrl: request.CoverImageUrl,
            price: request.Price,
            deliveryUrl: request.DeliveryUrl,
            defaultAffiliateCommissionPercent: request.DefaultAffiliateCommissionPercent,
            affiliationMode: request.AffiliationMode,
            splitRuleId: request.SplitRuleId,
            category: request.Category,
            facebookPixelId: request.FacebookPixelId,
            googleAdsConversionId: request.GoogleAdsConversionId);

        productRepository.Update(product);
        await productRepository.SaveChangesAsync();
        return await ToDtoAsync(product);
    }

    public async Task<ProductDto?> GetByIdAsync(Guid tenantId, Guid productId)
    {
        var p = await productRepository.GetByIdAsync(tenantId, productId);
        return p is null ? null : await ToDtoAsync(p);
    }

    public async Task<ProductDto?> GetBySlugAsync(Guid tenantId, string slug)
    {
        var p = await productRepository.GetBySlugAsync(tenantId, NormalizeSlug(slug));
        return p is null ? null : await ToDtoAsync(p);
    }

    public async Task<ProductListDto> ListByOwnerAsync(Guid tenantId, Guid ownerSellerId, ProductStatus? status, int page, int pageSize)
    {
        var (skip, take) = Paginate(page, pageSize);
        var (items, total) = await productRepository.GetByOwnerAsync(tenantId, ownerSellerId, status, skip, take);

        // Carrega métricas de toda a página em duas queries agregadas — uma
        // pra TX, outra pra afiliações. N+1 evitado via GROUP BY na DB.
        var productIds = items.Select(p => p.Id).ToList();
        var metricsByProduct = productIds.Count == 0
            ? new Dictionary<Guid, ProductMetricsSnapshot>()
            : await productRepository.GetMetricsForProductsAsync(tenantId, productIds);

        var dtos = new List<ProductDto>(items.Count);
        foreach (var p in items)
        {
            var dto = await ToDtoAsync(p);
            // Snapshot do repo já tem zeros pra produtos sem atividade — então
            // sempre vai existir no dicionário (preenchido com defaults). Só
            // mapeia pro DTO se a chave existir defensivamente.
            if (metricsByProduct.TryGetValue(p.Id, out var snap))
                dto = dto with { Metrics = new ProductMetricsDto(snap.Sales30d, snap.Volume30d, snap.ActiveAffiliates, snap.SalesByDay) };
            dtos.Add(dto);
        }
        return new ProductListDto(dtos, total);
    }

    // === Assets / materiais de divulgação ===

    public async Task<ProductAssetDto> AddAssetAsync(
        Guid tenantId, Guid ownerSellerId, Guid productId, CreateProductAssetDto request)
    {
        var product = await productRepository.GetByIdAsync(tenantId, productId)
            ?? throw new NotFoundException("Product.NotFound", $"Produto {productId} não encontrado.");
        AssertOwnership(product, ownerSellerId);

        var asset = ProductAsset.Create(
            tenantId: tenantId,
            productId: productId,
            title: request.Title,
            type: request.Type,
            url: request.Url,
            mimeType: request.MimeType,
            sizeBytes: request.SizeBytes);

        productRepository.AddAsset(asset);
        await productRepository.SaveChangesAsync();
        return ToAssetDto(asset);
    }

    public async Task<IReadOnlyList<ProductAssetDto>> ListAssetsAsync(Guid tenantId, Guid productId)
    {
        var items = await productRepository.ListAssetsAsync(tenantId, productId);
        return items.Select(ToAssetDto).ToList();
    }

    public async Task DeleteAssetAsync(Guid tenantId, Guid ownerSellerId, Guid assetId)
    {
        var asset = await productRepository.GetAssetByIdAsync(tenantId, assetId)
            ?? throw new NotFoundException("ProductAsset.NotFound", $"Asset {assetId} não encontrado.");
        var product = asset.Product ?? await productRepository.GetByIdAsync(tenantId, asset.ProductId);
        if (product is null)
            throw new NotFoundException("Product.NotFound", "Produto pai do asset não encontrado.");
        AssertOwnership(product, ownerSellerId);

        productRepository.RemoveAsset(asset);
        await productRepository.SaveChangesAsync();
    }

    private static ProductAssetDto ToAssetDto(ProductAsset a) =>
        new(
            Id: a.Id,
            ProductId: a.ProductId,
            Title: a.Title,
            Type: a.Type,
            Url: a.Url,
            MimeType: a.MimeType,
            SizeBytes: a.SizeBytes,
            CreatedAt: a.CreatedAt);

    public async Task<ProductStatsDto> GetOwnerStatsAsync(Guid tenantId, Guid ownerSellerId, int days = 30)
    {
        // Range 1..365 dias — afrouxado vs whitelist 7/30/90 antiga pra
        // permitir que o seller escolha qualquer janela. Cap em 365 evita
        // queries pesadas + arrays gigantes. Inválidos caem pra 30.
        if (days < 1 || days > 365) days = 30;

        var stats = await productRepository.GetOwnerStatsAsync(tenantId, ownerSellerId, days);
        return new ProductStatsDto(
            TotalProducts: stats.TotalProducts,
            PublishedCount: stats.PublishedCount,
            DraftCount: stats.DraftCount,
            PausedCount: stats.PausedCount,
            PeriodDays: stats.PeriodDays,
            SalesInPeriod: stats.SalesInPeriod,
            VolumeInPeriod: stats.VolumeInPeriod,
            PreviousSalesInPeriod: stats.PreviousSalesInPeriod,
            PreviousVolumeInPeriod: stats.PreviousVolumeInPeriod,
            SalesByDay: stats.SalesByDay,
            VolumeByDay: stats.VolumeByDay,
            CommissionsPaidInPeriod: stats.CommissionsPaidInPeriod,
            PreviousCommissionsPaidInPeriod: stats.PreviousCommissionsPaidInPeriod);
    }

    public async Task<ProductListDto> ListMarketplaceCatalogAsync(
        Guid tenantId, IReadOnlyList<string>? categories, decimal? minPrice, decimal? maxPrice, AffiliationMode? mode,
        int page, int pageSize, Guid? excludeOwnerSellerId = null)
    {
        var (skip, take) = Paginate(page, pageSize);
        var (items, total) = await productRepository.GetMarketplaceCatalogAsync(
            tenantId, categories, minPrice, maxPrice, mode, skip, take, excludeOwnerSellerId);

        // Carrega em batch o status de afiliação do caller para cada produto
        // exibido — assim o card já mostra "Aguardando" / "Afiliado" / "Recusado"
        // sem o usuário precisar clicar para descobrir. Único roundtrip extra.
        IReadOnlyDictionary<Guid, AffiliationStatus> existingByProduct =
            new Dictionary<Guid, AffiliationStatus>();
        if (excludeOwnerSellerId.HasValue && items.Count > 0)
        {
            existingByProduct = await affiliationRepository
                .GetStatusByProductIdsAndSellerAsync(
                    items.Select(p => p.Id).ToList(),
                    excludeOwnerSellerId.Value);
        }

        // Universo de categorias — independente do filtro de categoria do
        // caller. Alimenta o chip rail multi-select. Pulamos quando há filtros
        // de preço/modo aplicados para manter os chips alinhados ao escopo
        // visível atual? Tradeoff: hoje retornamos sempre o universo geral
        // para que chips fiquem estáveis. Pode evoluir para faceting completo.
        var categoryFacets = await productRepository.GetMarketplaceCategoriesAsync(
            tenantId, excludeOwnerSellerId);
        var availableCategories = categoryFacets
            .Select(c => new CategoryFacetDto(c.Name, c.Count))
            .ToList();

        var dtos = new List<ProductDto>(items.Count);
        foreach (var p in items)
        {
            var dto = await ToDtoAsync(p);
            if (existingByProduct.TryGetValue(p.Id, out var status))
                dto = dto with { CurrentSellerAffiliationStatus = (int)status };
            dtos.Add(dto);
        }
        return new ProductListDto(dtos, total, availableCategories);
    }

    public Task<ProductDto> PublishAsync(Guid tenantId, Guid ownerSellerId, Guid productId)
        => TransitionAsync(tenantId, ownerSellerId, productId, p => p.Publish());

    public Task<ProductDto> PauseAsync(Guid tenantId, Guid ownerSellerId, Guid productId)
        => TransitionAsync(tenantId, ownerSellerId, productId, p => p.Pause());

    public Task<ProductDto> ResumeAsync(Guid tenantId, Guid ownerSellerId, Guid productId)
        => TransitionAsync(tenantId, ownerSellerId, productId, p => p.Resume());

    public Task<ProductDto> ArchiveAsync(Guid tenantId, Guid ownerSellerId, Guid productId)
        => TransitionAsync(tenantId, ownerSellerId, productId, p => p.Archive());

    // --- internals ---

    private async Task<ProductDto> TransitionAsync(Guid tenantId, Guid ownerSellerId, Guid productId, Action<Product> transition)
    {
        var product = await productRepository.GetByIdAsync(tenantId, productId)
            ?? throw new NotFoundException("Product.NotFound", $"Produto {productId} não encontrado.");
        AssertOwnership(product, ownerSellerId);

        try { transition(product); }
        catch (InvalidOperationException ex) { throw new BusinessException("Product.InvalidTransition", ex.Message); }

        productRepository.Update(product);
        await productRepository.SaveChangesAsync();
        return await ToDtoAsync(product);
    }

    private static void AssertOwnership(Product product, Guid ownerSellerId)
    {
        if (product.OwnerSellerId != ownerSellerId)
            throw new BusinessException("Product.NotOwner",
                "Você não é o produtor deste produto.");
    }

    private static (int Skip, int Take) Paginate(int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;
        return ((page - 1) * pageSize, pageSize);
    }

    private async Task<ProductDto> ToDtoAsync(Product p)
    {
        string? ownerName = null;
        if (p.OwnerSeller is { } seller)
        {
            ownerName = seller.TradeName ?? seller.LegalName;
        }
        else
        {
            var s = await sellerRepository.GetByIdAsync(p.TenantId, p.OwnerSellerId);
            ownerName = s?.TradeName ?? s?.LegalName;
        }

        return new ProductDto(
            Id: p.Id,
            OwnerSellerId: p.OwnerSellerId,
            OwnerSellerName: ownerName,
            Name: p.Name,
            Slug: p.Slug,
            Description: p.Description,
            CoverImageUrl: p.CoverImageUrl,
            Price: p.Price,
            Currency: p.Currency,
            Type: (int)p.Type,
            DeliveryUrl: p.DeliveryUrl,
            DefaultAffiliateCommissionPercent: p.DefaultAffiliateCommissionPercent,
            AffiliationMode: (int)p.AffiliationMode,
            Status: (int)p.Status,
            SplitRuleId: p.SplitRuleId,
            Category: p.Category,
            FacebookPixelId: p.FacebookPixelId,
            GoogleAdsConversionId: p.GoogleAdsConversionId,
            CreatedAt: p.CreatedAt,
            UpdatedAt: p.UpdatedAt);
    }

    /// <summary>
    /// Slug URL-safe: lowercase, sem acento, hifenizado. Remove caracteres
    /// não-ASCII e colapsa whitespace + pontuação em hífens. Trim de hífens
    /// nas pontas. Resultado vazio se Name for só caracteres especiais —
    /// caller trata.
    /// </summary>
    private static string GenerateSlug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        // Normalize FormD separa acentos das letras; remove NonSpacingMarks.
        var normalized = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch);
        }
        var ascii = sb.ToString().Normalize(NormalizationForm.FormC);
        var result = new StringBuilder(ascii.Length);
        bool lastHyphen = false;
        foreach (var ch in ascii.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                result.Append(ch);
                lastHyphen = false;
            }
            else if (!lastHyphen && result.Length > 0)
            {
                result.Append('-');
                lastHyphen = true;
            }
        }
        return result.ToString().Trim('-');
    }

    private static string NormalizeSlug(string slug) =>
        GenerateSlug(slug); // mesma normalização — se usuário deu slug com acento, normalizamos.
}
