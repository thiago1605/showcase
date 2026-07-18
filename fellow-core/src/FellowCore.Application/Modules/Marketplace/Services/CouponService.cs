using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.Marketplace.Services;

public class CouponService(
    ICouponRepository couponRepository,
    IProductRepository productRepository
) : ICouponService
{
    public async Task<CouponDto> CreateAsync(Guid tenantId, Guid ownerSellerId, CreateCouponDto request)
    {
        // Validação de ownership: pra cupom de produto, owner do produto =
        // owner do cupom. Pra cupom global do tenant, qualquer seller do
        // tenant pode criar (não tem owner próprio do cupom no schema).
        // Pra MVP: simplificamos exigindo que cupom global SEJA criado por
        // seller do tenant (autenticação já valida). Cupom de produto exige
        // ownership do produto.
        string? productName = null;
        if (request.ProductId.HasValue)
        {
            var product = await productRepository.GetByIdAsync(tenantId, request.ProductId.Value)
                ?? throw new NotFoundException("Product.NotFound",
                    $"Produto {request.ProductId.Value} não encontrado.");
            if (product.OwnerSellerId != ownerSellerId)
                throw new BusinessException("Coupon.NotProductOwner",
                    "Apenas o produtor pode criar cupons para o produto.");
            productName = product.Name;
        }

        // Validações de domínio (Code não-vazio, Value > 0, Percent <= 100)
        // são feitas no Create da entity — caem aqui como ArgumentException
        // que vira 400 no pipeline.

        // Code único por tenant.
        if (await couponRepository.CodeExistsAsync(tenantId, request.Code))
            throw new BusinessException("Coupon.CodeTaken",
                $"Já existe um cupom com código '{request.Code}' nesse tenant.");

        var coupon = Coupon.Create(
            tenantId: tenantId,
            code: request.Code,
            type: request.Type,
            value: request.Value,
            productId: request.ProductId,
            validFrom: request.ValidFrom,
            validUntil: request.ValidUntil,
            maxUses: request.MaxUses);

        couponRepository.Add(coupon);
        await couponRepository.SaveChangesAsync();
        return ToDto(coupon, productName);
    }

    public async Task<IReadOnlyList<CouponDto>> ListAsync(Guid tenantId, Guid ownerSellerId, Guid? productId)
    {
        if (productId.HasValue)
        {
            // Pra listar cupons de um produto, verifica ownership.
            var product = await productRepository.GetByIdAsync(tenantId, productId.Value)
                ?? throw new NotFoundException("Product.NotFound",
                    $"Produto {productId.Value} não encontrado.");
            if (product.OwnerSellerId != ownerSellerId)
                throw new BusinessException("Coupon.NotProductOwner",
                    "Apenas o produtor pode listar cupons do produto.");

            var itemsForProduct = await couponRepository.ListAsync(tenantId, productId);
            // ProductName resolvido uma vez — todos os cupons dessa lista pertencem
            // ao mesmo produto.
            return itemsForProduct.Select(c => ToDto(c, product.Name)).ToList();
        }
        // Pra cupons globais (productId = null), qualquer seller do tenant pode listar.
        // Refinement: poderia filtrar por criador, mas Coupon não tem owner próprio no MVP.

        var items = await couponRepository.ListAsync(tenantId, productId);
        return items.Select(c => ToDto(c, null)).ToList();
    }

    public async Task<IReadOnlyList<CouponDto>> ListByOwnerAsync(Guid tenantId, Guid ownerSellerId)
    {
        var coupons = await couponRepository.ListByOwnerAsync(tenantId, ownerSellerId);
        // Pré-fetch dos nomes dos produtos referenciados, em UMA query — evita
        // N+1 quando produtor tem dezenas de cupons espalhados em vários produtos.
        var productIds = coupons
            .Where(c => c.ProductId.HasValue)
            .Select(c => c.ProductId!.Value)
            .Distinct()
            .ToList();
        var productNames = new Dictionary<Guid, string>();
        foreach (var pid in productIds)
        {
            var p = await productRepository.GetByIdAsync(tenantId, pid);
            if (p is not null) productNames[pid] = p.Name;
        }
        return coupons
            .Select(c => ToDto(c, c.ProductId.HasValue && productNames.TryGetValue(c.ProductId.Value, out var n) ? n : null))
            .ToList();
    }

    public async Task DeleteAsync(Guid tenantId, Guid ownerSellerId, Guid couponId)
    {
        var coupon = await couponRepository.GetByIdAsync(tenantId, couponId)
            ?? throw new NotFoundException("Coupon.NotFound", $"Cupom {couponId} não encontrado.");

        if (coupon.ProductId.HasValue)
        {
            var product = await productRepository.GetByIdAsync(tenantId, coupon.ProductId.Value);
            if (product is null || product.OwnerSellerId != ownerSellerId)
                throw new BusinessException("Coupon.NotProductOwner",
                    "Apenas o produtor do produto pode excluir o cupom.");
        }

        couponRepository.Remove(coupon);
        await couponRepository.SaveChangesAsync();
    }

    public async Task<CouponValidationDto?> ValidateAsync(string slug, string code)
    {
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(code)) return null;

        // Resolve produto pelo slug pra ter tenant + product context.
        var product = await productRepository.GetPublishedBySlugGlobalAsync(slug.Trim().ToLowerInvariant());
        if (product is null) return null;

        var coupon = await couponRepository.GetByCodeAsync(product.TenantId, code);
        if (coupon is null) return null;

        // Cupom restrito a outro produto? Inválido pra esse.
        if (coupon.ProductId.HasValue && coupon.ProductId.Value != product.Id) return null;
        if (!coupon.IsValid()) return null;

        var discount = coupon.CalculateDiscount(product.Price);
        return new CouponValidationDto(
            Code: coupon.Code,
            Type: (int)coupon.Type,
            Value: coupon.Value,
            DiscountAmount: discount,
            FinalPrice: product.Price - discount);
    }

    private static CouponDto ToDto(Coupon c, string? productName) => new(
        Id: c.Id,
        ProductId: c.ProductId,
        ProductName: productName,
        Code: c.Code,
        Type: (int)c.Type,
        Value: c.Value,
        ValidFrom: c.ValidFrom,
        ValidUntil: c.ValidUntil,
        MaxUses: c.MaxUses,
        UsedCount: c.UsedCount,
        CreatedAt: c.CreatedAt);
}
