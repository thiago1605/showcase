using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.Marketplace.Services;

public class ProductOrderBumpService(
    IProductOrderBumpRepository bumpRepository,
    IProductRepository productRepository
) : IProductOrderBumpService
{
    /// <summary>Limite duro de bumps ativos por produto principal — espelha
    /// Kirvano/Hotmart e evita overwhelm visual no checkout. Alterar aqui
    /// + revisar a UI (que renderiza badge "X/3").</summary>
    private const int MaxActiveBumpsPerProduct = 3;

    public async Task<IReadOnlyList<OrderBumpDto>> ListAsync(Guid tenantId, Guid ownerSellerId, Guid mainProductId)
    {
        var main = await productRepository.GetByIdAsync(tenantId, mainProductId)
            ?? throw new NotFoundException("Product.NotFound", $"Produto {mainProductId} não encontrado.");
        AssertOwnership(main, ownerSellerId);

        var bumps = await bumpRepository.ListByMainProductAsync(tenantId, mainProductId);
        return bumps.Select(ToDto).ToList();
    }

    public async Task<OrderBumpDto> CreateAsync(
        Guid tenantId, Guid ownerSellerId, Guid mainProductId, CreateOrderBumpDto request)
    {
        var main = await productRepository.GetByIdAsync(tenantId, mainProductId)
            ?? throw new NotFoundException("Product.NotFound", $"Produto principal {mainProductId} não encontrado.");
        AssertOwnership(main, ownerSellerId);

        if (request.BumpProductId == mainProductId)
            throw new BusinessException("OrderBump.SelfReference",
                "O produto ofertado como bump não pode ser o próprio produto principal.");

        var bump = await productRepository.GetByIdAsync(tenantId, request.BumpProductId)
            ?? throw new NotFoundException("Product.NotFound",
                $"Produto a ser ofertado como bump ({request.BumpProductId}) não encontrado.");

        // Bump tem que ser do mesmo produtor — cross-seller bumps ficam pra v2
        // (precisa modelar split bump → bump seller separado).
        if (bump.OwnerSellerId != main.OwnerSellerId)
            throw new BusinessException("OrderBump.CrossSeller",
                "O produto ofertado como bump precisa ser do mesmo produtor.");

        // Dedup: mesmo bumpProductId já ativo no mesmo mainProduct quebra a
        // experiência (2 cards iguais no checkout). Permite recriar depois
        // de desativar — IsActive=false não bloqueia novo create.
        if (await bumpRepository.ExistsActivePairAsync(tenantId, mainProductId, request.BumpProductId))
            throw new BusinessException("OrderBump.Duplicate",
                "Este produto já está configurado como bump ativo. Edite o existente.");

        // Limite de 3 ativos. Conta antes do insert pra evitar precisar fazer
        // rollback se o save falhar.
        var activeCount = await bumpRepository.CountActiveByMainProductAsync(tenantId, mainProductId);
        if (activeCount >= MaxActiveBumpsPerProduct)
            throw new BusinessException("OrderBump.LimitReached",
                $"Máximo de {MaxActiveBumpsPerProduct} bumps ativos por produto. Desative algum antes de adicionar outro.");

        // DisplayOrder default = próximo slot. Se o caller mandou um valor
        // explícito, respeita (permite "insert no meio"). Caller é responsável
        // pelas colisões.
        var displayOrder = request.DisplayOrder ?? activeCount;

        // DiscountAmount: 0 (default) até o preço total do bump. Acima disso
        // resultaria em valor negativo no checkout — bloqueia aqui (entity só
        // valida >= 0 sem acesso ao Product.Price).
        var discountAmount = request.DiscountAmount ?? 0m;
        if (discountAmount > bump.Price)
            throw new BusinessException("OrderBump.DiscountExceedsPrice",
                $"O desconto (R$ {discountAmount:F2}) não pode ser maior que o preço do bump (R$ {bump.Price:F2}).");

        var entity = ProductOrderBump.Create(
            tenantId: tenantId,
            mainProductId: mainProductId,
            bumpProductId: request.BumpProductId,
            customTitle: request.CustomTitle,
            customDescription: request.CustomDescription,
            displayOrder: displayOrder,
            discountAmount: discountAmount);

        bumpRepository.Add(entity);
        await bumpRepository.SaveChangesAsync();

        // Re-fetch pra trazer com BumpProduct populado (Add não cuida do navigation).
        var refreshed = await bumpRepository.GetByIdAsync(tenantId, entity.Id);
        var list = refreshed is null
            ? new List<ProductOrderBump> { entity }
            : (List<ProductOrderBump>)[refreshed];
        // Se reload retornou sem BumpProduct (improvável mas defensivo), busca direto.
        var withBump = list[0].BumpProduct is not null
            ? list[0]
            : (await bumpRepository.ListByMainProductAsync(tenantId, mainProductId))
                .First(b => b.Id == entity.Id);
        return ToDto(withBump);
    }

    public async Task<OrderBumpDto> UpdateAsync(
        Guid tenantId, Guid ownerSellerId, Guid mainProductId, Guid bumpId, UpdateOrderBumpDto request)
    {
        var main = await productRepository.GetByIdAsync(tenantId, mainProductId)
            ?? throw new NotFoundException("Product.NotFound", $"Produto principal {mainProductId} não encontrado.");
        AssertOwnership(main, ownerSellerId);

        var entity = await bumpRepository.GetByIdAsync(tenantId, bumpId)
            ?? throw new NotFoundException("OrderBump.NotFound", $"Order bump {bumpId} não encontrado.");
        if (entity.MainProductId != mainProductId)
            throw new BusinessException("OrderBump.WrongMain",
                "Esse bump não pertence ao produto principal informado.");

        // Reativação: revalida o limite. Update só conta como reativação se
        // estava inativo e o request seta IsActive=true.
        if (request.IsActive == true && !entity.IsActive)
        {
            var activeCount = await bumpRepository.CountActiveByMainProductAsync(tenantId, mainProductId);
            if (activeCount >= MaxActiveBumpsPerProduct)
                throw new BusinessException("OrderBump.LimitReached",
                    $"Máximo de {MaxActiveBumpsPerProduct} bumps ativos. Desative algum antes de reativar este.");
        }

        // Valida desconto contra o preço atual do bump product (entity tem
        // acesso só ao valor cru). Mesma regra do Create: 0 <= discount <= price.
        if (request.DiscountAmount.HasValue)
        {
            var bumpPrice = entity.BumpProduct?.Price ?? 0m;
            if (request.DiscountAmount.Value > bumpPrice && bumpPrice > 0m)
                throw new BusinessException("OrderBump.DiscountExceedsPrice",
                    $"O desconto (R$ {request.DiscountAmount.Value:F2}) não pode ser maior que o preço do bump (R$ {bumpPrice:F2}).");
        }

        entity.Update(
            customTitle: request.CustomTitle,
            customDescription: request.CustomDescription,
            displayOrder: request.DisplayOrder,
            isActive: request.IsActive,
            discountAmount: request.DiscountAmount);

        bumpRepository.Update(entity);
        await bumpRepository.SaveChangesAsync();

        // Refresh pra obter o BumpProduct populado pro DTO.
        var refreshed = (await bumpRepository.ListByMainProductAsync(tenantId, mainProductId))
            .First(b => b.Id == entity.Id);
        return ToDto(refreshed);
    }

    public async Task DeleteAsync(Guid tenantId, Guid ownerSellerId, Guid mainProductId, Guid bumpId)
    {
        var main = await productRepository.GetByIdAsync(tenantId, mainProductId)
            ?? throw new NotFoundException("Product.NotFound", $"Produto principal {mainProductId} não encontrado.");
        AssertOwnership(main, ownerSellerId);

        var entity = await bumpRepository.GetByIdAsync(tenantId, bumpId)
            ?? throw new NotFoundException("OrderBump.NotFound", $"Order bump {bumpId} não encontrado.");
        if (entity.MainProductId != mainProductId)
            throw new BusinessException("OrderBump.WrongMain",
                "Esse bump não pertence ao produto principal informado.");

        bumpRepository.Remove(entity);
        await bumpRepository.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<PublicOrderBumpDto>?> ListPublicForSlugAsync(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var normalized = slug.Trim().ToLowerInvariant();

        // Single-tenant resolution: mesmo helper usado pelo MarketplaceCheckoutService.
        var main = await productRepository.GetPublishedBySlugGlobalAsync(normalized);
        if (main is null) return null;
        if (main.Status != ProductStatus.PUBLISHED) return null;

        var bumps = await bumpRepository.ListActiveByMainProductAsync(main.TenantId, main.Id);

        // Filtra bumps cujo produto referenciado não está PUBLISHED — produtor
        // pode ter pausado/arquivado o produto-bump sem mexer no bump. Política
        // de safety: nunca oferecer ao buyer algo que não pode ser comprado.
        var result = bumps
            .Where(b => b.BumpProduct is not null && b.BumpProduct.Status == ProductStatus.PUBLISHED)
            .Select(b =>
            {
                var price = b.BumpProduct!.Price;
                // Clamp defensivo — se o produtor reduziu o preço do bump abaixo
                // do desconto cadastrado, não cobra negativo: zera o final.
                var discount = b.DiscountAmount > price ? price : b.DiscountAmount;
                var finalPrice = price - discount;
                return new PublicOrderBumpDto(
                    Id: b.Id,
                    BumpProductId: b.BumpProductId,
                    Title: b.CustomTitle,
                    Description: b.CustomDescription,
                    Price: price,
                    Currency: b.BumpProduct!.Currency,
                    CoverImageUrl: b.BumpProduct!.CoverImageUrl,
                    DisplayOrder: b.DisplayOrder,
                    DiscountAmount: discount,
                    FinalPrice: finalPrice);
            })
            .ToList();
        return result;
    }

    private static void AssertOwnership(Product product, Guid ownerSellerId)
    {
        if (product.OwnerSellerId != ownerSellerId)
            throw new BusinessException("Product.NotOwner",
                "Você não é o produtor deste produto.");
    }

    private static OrderBumpDto ToDto(ProductOrderBump b)
    {
        var bp = b.BumpProduct;
        return new OrderBumpDto(
            Id: b.Id,
            MainProductId: b.MainProductId,
            BumpProductId: b.BumpProductId,
            BumpProductName: bp?.Name ?? string.Empty,
            BumpProductPrice: bp?.Price ?? 0m,
            BumpProductCoverImageUrl: bp?.CoverImageUrl,
            BumpProductStatus: (int)(bp?.Status ?? 0),
            CustomTitle: b.CustomTitle,
            CustomDescription: b.CustomDescription,
            DisplayOrder: b.DisplayOrder,
            IsActive: b.IsActive,
            CreatedAt: b.CreatedAt,
            UpdatedAt: b.UpdatedAt,
            DiscountAmount: b.DiscountAmount);
    }
}
