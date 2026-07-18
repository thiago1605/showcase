using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class ProductRepository(AppDbContext context) : IProductRepository
{
    public void Add(Product product) => context.Products.Add(product);
    public void Update(Product product) => context.Products.Update(product);
    public Task SaveChangesAsync() => context.SaveChangesAsync();

    public async Task<Product?> GetByIdAsync(Guid tenantId, Guid productId)
        => await context.Products
            .FirstOrDefaultAsync(p => p.Id == productId && p.TenantId == tenantId);

    public async Task<IReadOnlyList<Product>> GetByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> productIds)
    {
        // Early-return curto-circuita o round-trip quando o caller não tem
        // IDs pra resolver (ex.: dashboard com período vazio).
        if (productIds.Count == 0) return Array.Empty<Product>();

        return await context.Products
            .Where(p => p.TenantId == tenantId && productIds.Contains(p.Id))
            .ToListAsync();
    }

    public async Task<Product?> GetBySlugAsync(Guid tenantId, string slug)
        => await context.Products
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Slug == slug);

    public async Task<(IReadOnlyList<Product> Items, int TotalCount)> GetByOwnerAsync(
        Guid tenantId,
        Guid ownerSellerId,
        ProductStatus? status,
        int skip,
        int take)
    {
        var q = context.Products
            .Where(p => p.TenantId == tenantId && p.OwnerSellerId == ownerSellerId);
        if (status.HasValue) q = q.Where(p => p.Status == status.Value);

        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(p => p.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
        return (items, total);
    }

    public async Task<(IReadOnlyList<Product> Items, int TotalCount)> GetMarketplaceCatalogAsync(
        Guid tenantId,
        IReadOnlyList<string>? categories,
        decimal? minPrice,
        decimal? maxPrice,
        AffiliationMode? mode,
        int skip,
        int take,
        Guid? excludeOwnerSellerId = null)
    {
        // Catálogo de afiliação = produtos PUBLISHED com afiliação aberta
        // (OPEN ou REQUEST). CLOSED não aparece (não aceita afiliação).
        var q = context.Products.Where(p =>
            p.TenantId == tenantId &&
            p.Status == ProductStatus.PUBLISHED &&
            p.AffiliationMode != AffiliationMode.CLOSED);

        // Exclui produtos do próprio caller — ninguém afilia ao próprio produto.
        // Filtro composto com o índice (TenantId, OwnerSellerId), barato.
        if (excludeOwnerSellerId.HasValue)
            q = q.Where(p => p.OwnerSellerId != excludeOwnerSellerId.Value);

        if (categories is { Count: > 0 })
        {
            // Multi-select: OR entre as categorias selecionadas. Normalizamos
            // para lowercase no caller; aqui usamos comparação case-insensitive
            // via LOWER() para acomodar variações de capitalização no DB.
            var normalized = categories
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToLowerInvariant())
                .ToList();
            if (normalized.Count > 0)
                q = q.Where(p => p.Category != null &&
                    normalized.Contains(p.Category.ToLower()));
        }
        if (minPrice.HasValue) q = q.Where(p => p.Price >= minPrice.Value);
        if (maxPrice.HasValue) q = q.Where(p => p.Price <= maxPrice.Value);
        if (mode.HasValue) q = q.Where(p => p.AffiliationMode == mode.Value);

        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(p => p.DefaultAffiliateCommissionPercent)
            .ThenByDescending(p => p.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
        return (items, total);
    }

    public async Task<IReadOnlyList<(string Name, int Count)>> GetMarketplaceCategoriesAsync(
        Guid tenantId,
        Guid? excludeOwnerSellerId = null)
    {
        // Mesmo predicado base do catálogo (PUBLISHED + afiliação aberta +
        // excluir próprio seller), mas SEM filtro de categoria — esse é o
        // universo que alimenta os chips multi-select.
        var q = context.Products.Where(p =>
            p.TenantId == tenantId &&
            p.Status == ProductStatus.PUBLISHED &&
            p.AffiliationMode != AffiliationMode.CLOSED &&
            p.Category != null);

        if (excludeOwnerSellerId.HasValue)
            q = q.Where(p => p.OwnerSellerId != excludeOwnerSellerId.Value);

        // GROUP BY case-insensitive (LOWER) para deduplicar "Mentoria"/"mentoria".
        // O nome retornado é o primeiro encontrado (MIN preserva ordem alfabética
        // e privilegia a capitalização lexicograficamente menor, ex: "Ebook" antes
        // de "ebook"). Tradeoff aceitável; produtores são responsáveis por casing
        // consistente.
        var rows = await q
            .GroupBy(p => p.Category!.ToLower())
            .Select(g => new
            {
                Key = g.Key,
                Name = g.Min(p => p.Category!),
                Count = g.Count(),
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Name)
            .ToListAsync();

        return rows.Select(r => (r.Name, r.Count)).ToList();
    }

    // === Assets / materiais de divulgação ===

    public void AddAsset(ProductAsset asset) => context.ProductAssets.Add(asset);

    public Task<ProductAsset?> GetAssetByIdAsync(Guid tenantId, Guid assetId)
        => context.ProductAssets
            .Include(a => a.Product)
            .FirstOrDefaultAsync(a => a.Id == assetId && a.TenantId == tenantId);

    public async Task<IReadOnlyList<ProductAsset>> ListAssetsAsync(Guid tenantId, Guid productId)
        => await context.ProductAssets
            .Where(a => a.TenantId == tenantId && a.ProductId == productId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();

    public void RemoveAsset(ProductAsset asset) => context.ProductAssets.Remove(asset);

    public async Task<bool> SlugExistsAsync(Guid tenantId, string slug)
        => await context.Products
            .AnyAsync(p => p.TenantId == tenantId && p.Slug == slug);

    public async Task<Product?> GetPublishedBySlugGlobalAsync(string slug)
        => await context.Products
            .FirstOrDefaultAsync(p => p.Slug == slug && p.Status == ProductStatus.PUBLISHED);

    public async Task<ProductOwnerStats> GetOwnerStatsAsync(Guid tenantId, Guid ownerSellerId, int days)
    {
        // Janela móvel `days` (7/30/90 default 30). Caller valida o whitelist;
        // repo só defende contra valores absurdos.
        if (days < 1) days = 30;
        var since = DateTime.UtcNow.AddDays(-days);

        // Contagens por status — single roundtrip via GROUP BY. Em produtor
        // com 5-50 produtos, plano usa o índice (TenantId, OwnerSellerId)
        // criado em AddProductsAndAffiliations.
        var statusCounts = await context.Products
            .Where(p => p.TenantId == tenantId && p.OwnerSellerId == ownerSellerId)
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        var total = statusCounts.Sum(x => x.Count);
        int countOf(ProductStatus s) => statusCounts.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        // Transações capturadas dos produtos do produtor nos últimos 30d.
        // Ligação por ExternalReferenceId LIKE 'product:%' + SellerId == ownerSellerId
        // (produtor é o seller primário da TX no checkout marketplace). Volume
        // é Amount (gross) pq é o que o produtor "fez girar"; NetAmount é só do
        // que sobra após fees+splits — diferente conceito, deixamos pra "lucro".
        //
        // Usamos CreatedAt na janela (não UpdatedAt) pq UpdatedAt muda quando o
        // status transita (refund 35d depois empurraria a TX pra fora da janela).
        // Para checkout marketplace, CreatedAt ≈ momento da venda — gap de
        // segundos pro CAPTURED. Inclui CAPTURED e REFUNDED (sale aconteceu em
        // algum momento; refund posterior não apaga o fluxo bruto histórico).
        // Pega TXs dos últimos 2×days (atual + anterior) numa query só + raw
        // pra construir timeseries client-side. Volume típico: dezenas a
        // centenas de TX no horizonte 60-180d — barato carregar.
        var since60 = DateTime.UtcNow.AddDays(-days * 2);
        var txs = await context.Transactions
            .Where(t =>
                t.TenantId == tenantId &&
                t.SellerId == ownerSellerId &&
                t.ExternalReferenceId != null &&
                t.ExternalReferenceId.StartsWith("product:") &&
                t.CreatedAt >= since60 &&
                (t.Status == TransactionStatus.CAPTURED || t.Status == TransactionStatus.REFUNDED))
            .Select(t => new { t.Amount, t.CreatedAt })
            .ToListAsync();

        var current = txs.Where(t => t.CreatedAt >= since).ToList();
        var previous = txs.Where(t => t.CreatedAt < since).ToList();

        // Timeseries: tamanho do array == days. Buckets diários.
        var salesByDay = new int[days];
        var volumeByDay = new decimal[days];
        var today = DateTime.UtcNow.Date;
        foreach (var t in current)
        {
            var daysAgo = (int)(today - t.CreatedAt.Date).TotalDays;
            if (daysAgo < 0 || daysAgo >= days) continue;
            var idx = (days - 1) - daysAgo;
            salesByDay[idx]++;
            volumeByDay[idx] += t.Amount;
        }

        // Comissões pagas: SUM(SplitTransfer.Amount - ReversedAmount) onde:
        //   - TX é deste produtor (TX.SellerId == ownerSellerId)
        //   - TX é marketplace (ExternalReferenceId LIKE 'product:%')
        //   - SplitTransfer.IsPrimaryShare == false (= afiliado ou co-producer,
        //     não o próprio produtor recebendo o residual)
        //   - Status == PAID (já liquidado; pending/scheduled fica fora)
        // Filtra por SplitTransfer.CreatedAt na janela porque é quando o split
        // virou comprometido — alinhado com momento da venda.
        var splits = await context.SplitTransfers
            .Join(context.Transactions,
                  st => st.TransactionId,
                  t => t.Id,
                  (st, t) => new { Split = st, Tx = t })
            .Where(x =>
                x.Tx.TenantId == tenantId &&
                x.Tx.SellerId == ownerSellerId &&
                x.Tx.ExternalReferenceId != null &&
                x.Tx.ExternalReferenceId.StartsWith("product:") &&
                !x.Split.IsPrimaryShare &&
                x.Split.Status == SplitTransferStatus.PAID &&
                x.Split.CreatedAt >= since60)
            .Select(x => new
            {
                NetAmount = x.Split.Amount - x.Split.ReversedAmount,
                x.Split.CreatedAt,
            })
            .ToListAsync();

        var commissionsCurrent = splits.Where(s => s.CreatedAt >= since).Sum(s => s.NetAmount);
        var commissionsPrevious = splits.Where(s => s.CreatedAt < since).Sum(s => s.NetAmount);

        return new ProductOwnerStats(
            TotalProducts: total,
            PublishedCount: countOf(ProductStatus.PUBLISHED),
            DraftCount: countOf(ProductStatus.DRAFT),
            PausedCount: countOf(ProductStatus.PAUSED),
            PeriodDays: days,
            SalesInPeriod: current.Count,
            VolumeInPeriod: current.Sum(t => t.Amount),
            PreviousSalesInPeriod: previous.Count,
            PreviousVolumeInPeriod: previous.Sum(t => t.Amount),
            SalesByDay: salesByDay,
            VolumeByDay: volumeByDay,
            CommissionsPaidInPeriod: commissionsCurrent,
            PreviousCommissionsPaidInPeriod: commissionsPrevious);
    }

    public async Task<IReadOnlyDictionary<Guid, ProductMetricsSnapshot>> GetMetricsForProductsAsync(
        Guid tenantId, IReadOnlyList<Guid> productIds)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, ProductMetricsSnapshot>();

        var since = DateTime.UtcNow.AddDays(-30);

        // Pré-gera as strings de match pra ExternalReferenceId — converter na DB
        // (via String concat com p.Id::text) é mais fragil/portável; melhor
        // fazer client-side e usar IN().
        var refs = productIds.Select(id => $"product:{id}").ToList();

        // Carrega TXs CRUAS dos 30d (não agregadas) pra agregar client-side
        // tanto totais quanto timeseries diário. Pra produtor com poucas
        // dezenas de TX por página, é barato; evita 2 queries separadas.
        var txsRaw = await context.Transactions
            .Where(t =>
                t.TenantId == tenantId &&
                t.ExternalReferenceId != null &&
                refs.Contains(t.ExternalReferenceId) &&
                t.CreatedAt >= since &&
                (t.Status == TransactionStatus.CAPTURED || t.Status == TransactionStatus.REFUNDED))
            .Select(t => new { t.ExternalReferenceId, t.Amount, t.CreatedAt })
            .ToListAsync();

        // Afiliações ativas (APPROVED) — contagem por ProductId. Filtra só os
        // productIds da página pra não escanear afiliações de outros produtos.
        var affAggs = await context.Affiliations
            .Where(a =>
                a.TenantId == tenantId &&
                a.Status == AffiliationStatus.APPROVED &&
                productIds.Contains(a.ProductId))
            .GroupBy(a => a.ProductId)
            .Select(g => new { ProductId = g.Key, Count = g.Count() })
            .ToListAsync();

        // Merge dos agregados num único dicionário por productId. Default zero
        // pra produtos sem atividade.
        var byProduct = new Dictionary<Guid, ProductMetricsSnapshot>(productIds.Count);
        foreach (var pid in productIds)
            byProduct[pid] = new ProductMetricsSnapshot(pid, 0, 0m, 0, new int[30]);

        // Agrega TX por produto: count, volume, timeseries diário.
        const string prefix = "product:";
        var today = DateTime.UtcNow.Date;
        foreach (var tx in txsRaw)
        {
            if (tx.ExternalReferenceId is null || tx.ExternalReferenceId.Length <= prefix.Length) continue;
            if (!Guid.TryParse(tx.ExternalReferenceId.AsSpan(prefix.Length), out var pid)) continue;
            if (!byProduct.TryGetValue(pid, out var cur)) continue;

            var newSales = cur.Sales30d + 1;
            var newVolume = cur.Volume30d + tx.Amount;
            var newSeries = cur.SalesByDay;
            var daysAgo = (int)(today - tx.CreatedAt.Date).TotalDays;
            if (daysAgo >= 0 && daysAgo < 30)
            {
                // Clona array antes de mutar — record `with` não faz deep copy.
                newSeries = (int[])cur.SalesByDay.Clone();
                newSeries[29 - daysAgo]++;
            }
            byProduct[pid] = cur with { Sales30d = newSales, Volume30d = newVolume, SalesByDay = newSeries };
        }

        foreach (var aff in affAggs)
        {
            if (!byProduct.TryGetValue(aff.ProductId, out var cur)) continue;
            byProduct[aff.ProductId] = cur with { ActiveAffiliates = aff.Count };
        }

        return byProduct;
    }
}
