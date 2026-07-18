using System.Security.Cryptography;
using System.Text;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Marketplace.Services;

public class MarketplaceCheckoutService(
    IProductRepository productRepository,
    IAffiliationRepository affiliationRepository,
    ISellerRepository sellerRepository,
    ITransactionService transactionService,
    ITransactionRepository transactionRepository,
    ISplitRuleRepository splitRuleRepository,
    ICouponRepository couponRepository,
    IProductOrderBumpRepository orderBumpRepository,
    ILogger<MarketplaceCheckoutService> logger
) : IMarketplaceCheckoutService
{
    // Status considerados "terminais" — não vão mudar mais. O polling do
    // frontend pode parar de tentar quando atinge um deles.
    private static readonly HashSet<TransactionStatus> TerminalStatuses =
    [
        TransactionStatus.CAPTURED,
        TransactionStatus.FAILED,
        TransactionStatus.VOIDED,
        TransactionStatus.REFUNDED,
        TransactionStatus.DECLINED,
    ];

    public async Task RecordAffiliateClickAsync(
        string trackingCode,
        string ipAddress,
        string userAgent,
        string? referrer)
    {
        if (string.IsNullOrWhiteSpace(trackingCode)) return;

        // Resolve afiliação via tracking code. Só conta click se a afiliação
        // estiver ativa (APPROVED) — tracking de PENDING/REJECTED/REVOKED não
        // converte mesmo, então tracking de clicks pra eles seria ruído.
        var aff = await affiliationRepository.GetByTrackingCodeAsync(trackingCode.Trim());
        if (aff is null || !aff.IsActive) return;

        // Fingerprint = SHA256 trunc(IP + UA). Hash one-way evita guardar PII
        // identificável (IP + UA seria PII por LGPD). 32 chars hex = 128 bits,
        // colisão esperada acima de 2^64 entries — espaço seguro.
        var fingerprint = ComputeFingerprint(ipAddress, userAgent);

        // Extrai só o host do referrer (sem path/query) — economiza espaço e
        // protege contra exposição inadvertida de URLs com PII.
        string? referrerHost = null;
        if (!string.IsNullOrWhiteSpace(referrer))
        {
            try
            {
                var uri = new Uri(referrer);
                referrerHost = uri.Host;
                if (referrerHost.Length > 200) referrerHost = referrerHost[..200];
            }
            catch
            {
                /* referrer mal-formado — ignora */
            }
        }

        var click = AffiliateClickEvent.Create(
            tenantId: aff.TenantId,
            affiliationId: aff.Id,
            productId: aff.ProductId,
            affiliateSellerId: aff.AffiliateSellerId,
            fingerprint: fingerprint,
            refererHost: referrerHost);

        var recorded = await affiliationRepository.RecordClickAsync(click);
        if (!recorded)
        {
            logger.LogDebug(
                "[AFFILIATE_CLICK] Suprimido como duplicata (mesma fingerprint < 1h) — affId={AffId}",
                aff.Id);
        }
    }

    private static string ComputeFingerprint(string ipAddress, string userAgent)
    {
        var input = $"{ipAddress ?? ""}|{userAgent ?? ""}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        // 32 chars hex = 128 bits — suficiente pra fingerprint não-criptográfico.
        var sb = new StringBuilder(32);
        for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    public async Task<PublicTransactionStatusDto?> GetStatusAsync(string slug, Guid transactionId)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var product = await FindPublishedBySlugAnyTenantAsync(slug.Trim().ToLowerInvariant());
        if (product is null) return null;
        var tx = await transactionRepository.GetByIdAsync(product.TenantId, transactionId);
        if (tx is null) return null;
        // Validação extra: a TX precisa ter sido criada pra ESTE produto
        // (ExternalReferenceId casa com "product:{Product.Id}"). Sem isso, um
        // atacante poderia tentar polling de TXs de outros produtos no mesmo
        // tenant chutando GUIDs.
        if (tx.ExternalReferenceId != $"product:{product.Id}") return null;
        return new PublicTransactionStatusDto(
            Id: tx.Id,
            Status: tx.Status.ToString(),
            IsTerminal: TerminalStatuses.Contains(tx.Status));
    }

    public async Task<PublicProductDto?> ResolveAsync(string slug, string? trackingCode)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var normalizedSlug = slug.Trim().ToLowerInvariant();

        // Single-tenant resolution: pra MVP, varremos pela primeira ocorrência
        // do slug em PUBLISHED. Multi-tenant precisa de subdomínio/host pra
        // disambiguar (vai vir como melhoria).
        var product = await FindPublishedBySlugAnyTenantAsync(normalizedSlug);
        if (product is null) return null;
        if (product.Status != ProductStatus.PUBLISHED) return null;

        var producer = await sellerRepository.GetByIdAsync(product.TenantId, product.OwnerSellerId);
        var producerName = producer?.TradeName ?? producer?.LegalName;

        PublicAffiliateInfoDto? affInfo = null;
        if (!string.IsNullOrWhiteSpace(trackingCode))
        {
            var aff = await affiliationRepository.GetByTrackingCodeAsync(trackingCode.Trim());
            if (aff is not null
                && aff.IsActive
                && aff.ProductId == product.Id
                && aff.TenantId == product.TenantId)
            {
                var affSeller = await sellerRepository.GetByIdAsync(aff.TenantId, aff.AffiliateSellerId);
                affInfo = new PublicAffiliateInfoDto(
                    TrackingCode: aff.TrackingCode,
                    AffiliateName: affSeller?.TradeName ?? affSeller?.LegalName,
                    CommissionPercent: aff.EffectiveCommissionPercent(product.DefaultAffiliateCommissionPercent));
            }
        }

        return new PublicProductDto(
            Id: product.Id,
            Name: product.Name,
            Slug: product.Slug,
            Description: product.Description,
            CoverImageUrl: product.CoverImageUrl,
            Price: product.Price,
            Currency: product.Currency,
            Type: (int)product.Type,
            Category: product.Category,
            ProducerName: producerName,
            Affiliate: affInfo,
            FacebookPixelId: product.FacebookPixelId,
            GoogleAdsConversionId: product.GoogleAdsConversionId);
    }

    public async Task<TransactionResponseDto> CheckoutAsync(string slug, PublicCheckoutRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new BusinessException("Checkout.InvalidSlug", "Slug é obrigatório.");

        var product = await FindPublishedBySlugAnyTenantAsync(slug.Trim().ToLowerInvariant())
            ?? throw new NotFoundException("Product.NotFound", "Produto não encontrado ou indisponível.");
        if (product.Status != ProductStatus.PUBLISHED)
            throw new BusinessException("Checkout.NotPublished", "Este produto não está aceitando compras no momento.");

        // Resolve affiliate (opcional). Tracking code inválido NÃO bloqueia a
        // compra — só pula o split (atribuição last-click padrão). Isso evita
        // perder venda por link quebrado/expirado.
        Affiliation? affiliation = null;
        if (!string.IsNullOrWhiteSpace(request.TrackingCode))
        {
            var aff = await affiliationRepository.GetByTrackingCodeAsync(request.TrackingCode.Trim());
            if (aff is not null
                && aff.IsActive
                && aff.ProductId == product.Id
                && aff.TenantId == product.TenantId
                && aff.AffiliateSellerId != product.OwnerSellerId) // auto-affiliation guard
            {
                affiliation = aff;
            }
            else
            {
                logger.LogInformation(
                    "[MARKETPLACE_CHECKOUT] Tracking code {Code} não atribuível pro produto {ProductId} — venda segue sem split.",
                    request.TrackingCode, product.Id);
            }
        }

        // Resolve cupom (se aplicado). Inválido = ignora silenciosamente — não
        // bloqueia a compra, mesma postura do tracking code. Apenas o desconto
        // não é aplicado, e log de info pra observabilidade.
        decimal discount = 0m;
        Coupon? appliedCoupon = null;
        if (!string.IsNullOrWhiteSpace(request.CouponCode))
        {
            var coupon = await couponRepository.GetByCodeAsync(product.TenantId, request.CouponCode.Trim());
            if (coupon is not null
                && coupon.IsValid()
                && (!coupon.ProductId.HasValue || coupon.ProductId.Value == product.Id))
            {
                discount = coupon.CalculateDiscount(product.Price);
                appliedCoupon = coupon;
            }
            else
            {
                logger.LogInformation(
                    "[MARKETPLACE_CHECKOUT] Cupom {Code} inválido pra produto {ProductId} — venda segue sem desconto.",
                    request.CouponCode, product.Id);
            }
        }

        // Preço do main product pós-cupom. Cupom incide só sobre o produto
        // principal — bumps mantêm preço próprio (política Kirvano-like, evita
        // cupons de 100% drenarem bumps acidentalmente).
        var mainPriceAfterDiscount = product.Price - discount;
        if (mainPriceAfterDiscount < 0.01m)
        {
            // Cupom não pode zerar a venda — fallback de segurança contra cupom
            // FIXED >= price. CalculateDiscount já faz cap, mas duplo-check.
            mainPriceAfterDiscount = product.Price;
            discount = 0m;
            appliedCoupon = null;
        }

        // Resolve order bumps selecionados pelo buyer. Política: bumps inválidos
        // são IGNORADOS silenciosamente (igual tracking code / cupom inválido) —
        // não bloqueia a compra. Inválidos: id que não existe, não está ativo,
        // bump-product não está PUBLISHED, id duplicado no array.
        //
        // Cada bump válido contribui com seu próprio Price ao total cobrado.
        // PIX/Boleto/Cartão: paga 1 valor único, mas Transaction.Items[] carrega
        // o breakdown pra fiscal/conciliação/receipts.
        var selectedBumps = new List<ProductOrderBump>();
        if (request.BumpProductIds is { Count: > 0 })
        {
            var activeBumps = await orderBumpRepository.ListActiveByMainProductAsync(product.TenantId, product.Id);
            var validById = activeBumps.ToDictionary(b => b.Id, b => b);
            var seen = new HashSet<Guid>();
            foreach (var bumpId in request.BumpProductIds)
            {
                if (!seen.Add(bumpId)) continue; // dedup do array
                if (!validById.TryGetValue(bumpId, out var bump))
                {
                    logger.LogInformation(
                        "[MARKETPLACE_CHECKOUT] Bump {BumpId} ignorado: não está ativo no produto {ProductId}.",
                        bumpId, product.Id);
                    continue;
                }
                // Bump product PUBLISHED check — produtor pode ter pausado/arquivado
                // o produto-bump sem desativar o bump entry. Safety: nunca cobra
                // por algo não publicado.
                if (bump.BumpProduct is null || bump.BumpProduct.Status != ProductStatus.PUBLISHED)
                {
                    logger.LogInformation(
                        "[MARKETPLACE_CHECKOUT] Bump {BumpId} ignorado: bumpProduct não está PUBLISHED.",
                        bumpId);
                    continue;
                }
                selectedBumps.Add(bump);
            }
        }

        // Preço efetivo de cada bump = Price - DiscountAmount (clamp em 0 se
        // produtor baixou o preço abaixo do desconto registrado).
        static decimal EffectiveBumpPrice(ProductOrderBump b)
        {
            var price = b.BumpProduct!.Price;
            var discount = b.DiscountAmount > price ? price : b.DiscountAmount;
            return price - discount;
        }
        var bumpsTotal = selectedBumps.Sum(EffectiveBumpPrice);
        var effectivePrice = mainPriceAfterDiscount + bumpsTotal;

        // Builds SplitDtos[]:
        //  - Affiliate: amount fixo = effectivePrice × commission% (não % do dto
        //    pra evitar drift de rounding entre aqui e dentro do TransactionService)
        //  - Co-producers (Product.SplitRuleId): aplicam-se no REMAINDER após
        //    extrair a comissão do afiliado. Cada recipient leva sua fatia
        //    (% do remainder ou amount fixo).
        //  - Producer (owner): residual implícito via primary seller (Transaction.SellerId)
        var splits = new List<SplitDto>();
        decimal commissionAmount = 0m;
        if (affiliation is not null)
        {
            var commissionPct = affiliation.EffectiveCommissionPercent(product.DefaultAffiliateCommissionPercent);
            commissionAmount = Math.Round(effectivePrice * commissionPct / 100m, 2, MidpointRounding.AwayFromZero);
            if (commissionAmount > 0 && commissionAmount < effectivePrice)
            {
                splits.Add(new SplitDto(
                    SellerId: affiliation.AffiliateSellerId,
                    Amount: commissionAmount,
                    Percentage: null));
            }
            else
            {
                logger.LogWarning(
                    "[MARKETPLACE_CHECKOUT] Commission amount {Amount} inválido pra produto {ProductId} (effectivePrice {Price}) — venda sem split.",
                    commissionAmount, product.Id, effectivePrice);
                commissionAmount = 0m;
            }
        }

        // Co-produção: se o Product tem SplitRuleId, aplica a rule no remainder.
        // Carrega rule com recipients pra calcular as fatias. Excluí o próprio
        // owner-seller do rateio (residual já vai pra ele naturalmente).
        if (product.SplitRuleId.HasValue)
        {
            var rule = await splitRuleRepository.GetByIdWithRecipientsAsync(product.TenantId, product.SplitRuleId.Value);
            if (rule is not null)
            {
                // Remainder = preço efetivo (pós-desconto) - comissão affiliate.
                // Co-producers absorvem o desconto proporcionalmente igual o
                // produtor — política simples e consistente.
                var remainder = effectivePrice - commissionAmount;
                // Recipients ordenados por priority (fixed amounts primeiro = priority menor
                // por convenção). Cada um pega sua fatia DO REMAINDER (não do gross).
                foreach (var rec in rule.Recipients.OrderBy(r => r.Priority))
                {
                    // Pula o owner — ele já é o primary seller e leva o residual.
                    if (rec.SellerId == product.OwnerSellerId) continue;

                    decimal recAmount = 0m;
                    if (rec.FixedAmount.HasValue && rec.FixedAmount.Value > 0)
                    {
                        // Fixed amount: leva exatamente esse valor (se cabe no remainder).
                        recAmount = Math.Min(rec.FixedAmount.Value, remainder);
                    }
                    else if (rec.Percentage.HasValue && rec.Percentage.Value > 0)
                    {
                        // Percentage: % do remainder pós-affiliate.
                        recAmount = Math.Round(
                            remainder * rec.Percentage.Value / 100m,
                            2, MidpointRounding.AwayFromZero);
                    }

                    if (recAmount > 0 && recAmount <= remainder)
                    {
                        splits.Add(new SplitDto(
                            SellerId: rec.SellerId,
                            Amount: recAmount,
                            Percentage: null));
                        remainder -= recAmount;
                    }
                }
            }
            else
            {
                logger.LogWarning(
                    "[MARKETPLACE_CHECKOUT] Product {ProductId} aponta pra SplitRule {RuleId} que não existe — venda sem co-produção.",
                    product.Id, product.SplitRuleId.Value);
            }
        }

        var description = $"Marketplace: {product.Name}";
        // PayerDto requer Name/Document/Email não-null. Pra checkout público,
        // o frontend valida obrigatoriedade no form — backend só repassa.
        // Empty string como fallback defensivo evita NullRef se request vier
        // mal formado; validação do TransactionService dispara erro semântico.
        var payer = new PayerDto(
            Name: request.PayerName ?? string.Empty,
            Document: request.PayerDocument ?? string.Empty,
            Email: request.PayerEmail ?? string.Empty,
            Phone: request.PayerPhone);

        // Monta metadata com UTM + tracking — só inclui chaves não-vazias pra não
        // poluir o JSON persistido. Prefix "utm_*" + "*click_id" facilita filtro
        // futuro tipo `WHERE Metadata->>'utm_source' = 'instagram'`.
        var metadata = BuildTrackingMetadata(request);

        // Se aplicou cupom, registra na metadata pra rastreabilidade e
        // possíveis relatórios futuros (quantas vendas por cupom, etc.).
        if (appliedCoupon is not null)
        {
            metadata["coupon_code"] = appliedCoupon.Code;
            metadata["coupon_discount"] = discount.ToString("F2");
        }

        // Items[]: só populamos quando há bumps. Sem bumps, mantém Items=null
        // (comportamento legacy — TX representa só o main product, sem breakdown
        // por item). Com bumps, populamos main + cada bump separado pra fiscal /
        // receipt / conciliação. Bumps carregam ProductId="bump:<id>" pra
        // facilitar filtro posterior.
        //
        // IMPORTANTE: Items[] aqui é só auditoria. Splits já foram calculados
        // sobre `effectivePrice` (= main + bumps) — afiliado/co-producers
        // recebem proporcional ao total, incluindo bumps. Isso é o
        // comportamento esperado pelo Kirvano (afiliado leva % sobre bumps
        // também — incentiva o afiliado a promover sem se importar com bump).
        List<TransactionItemDto>? items = null;
        if (selectedBumps.Count > 0)
        {
            items = new List<TransactionItemDto>(selectedBumps.Count + 1)
            {
                new(
                    Description: product.Name,
                    Quantity: 1,
                    UnitAmount: mainPriceAfterDiscount,
                    ProductId: $"product:{product.Id}",
                    SellerId: product.OwnerSellerId,
                    SplitRuleId: null)
            };
            foreach (var b in selectedBumps)
            {
                items.Add(new TransactionItemDto(
                    Description: b.BumpProduct!.Name,
                    Quantity: 1,
                    UnitAmount: EffectiveBumpPrice(b),
                    ProductId: $"bump:{b.BumpProductId}",
                    SellerId: product.OwnerSellerId, // bump é do mesmo seller
                    SplitRuleId: null));
            }
            // Metadata flag pra facilitar busca de TXs com bumps (sem precisar
            // JOIN com Items). Lista os ids selecionados pra auditoria.
            metadata["has_bumps"] = "true";
            metadata["bump_count"] = selectedBumps.Count.ToString();
            metadata["bump_total"] = bumpsTotal.ToString("F2");
        }

        var createDto = new CreateTransactionDto(
            SellerId: product.OwnerSellerId,
            Amount: effectivePrice, // preço final cobrado (main pós-cupom + bumps)
            PaymentType: request.PaymentType,
            Installments: request.Installments ?? 1,
            Description: description,
            Payer: payer,
            IdempotencyKey: null,
            ExternalReferenceId: $"product:{product.Id}",
            Splits: splits.Count > 0 ? splits : null,
            SplitRuleId: null, // SplitRule já resolvida inline em splits[]
            FeeAllocationPolicy: null,
            Items: items,
            AdvanceOptIn: null,
            Metadata: metadata.Count > 0 ? metadata : null);

        var result = await transactionService.CreateAsync(product.TenantId, createDto);

        // Incrementa contador de uso do cupom DEPOIS da TX criada com sucesso.
        // Sem retry se falhar — race condition aqui é raro (mesmo cupom usado 2x
        // simultaneamente). Se virar problema, mudar pra ExecuteUpdateAsync com
        // CAS ou serializar via outbox.
        if (appliedCoupon is not null)
        {
            try
            {
                appliedCoupon.IncrementUsage();
                couponRepository.Update(appliedCoupon);
                await couponRepository.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[MARKETPLACE_CHECKOUT] Falha ao incrementar usage do cupom {Code} — TX já criada.",
                    appliedCoupon.Code);
            }
        }

        return result;
    }

    /// <summary>
    /// Constrói o dicionário de metadata a partir dos campos UTM/tracking do
    /// request. Só inclui chaves cujo valor é não-vazio — evita persistir
    /// `{"utm_source": "", "utm_medium": ""}` que polui o JSON sem ganho.
    ///
    /// Os campos seguem o naming convention do Google Analytics (utm_*) +
    /// gclid/fbclid pros click IDs específicos. `referrer` cobre o caso de
    /// tráfego direto sem UTM mas com origem identificável.
    /// </summary>
    private static Dictionary<string, string> BuildTrackingMetadata(PublicCheckoutRequestDto request)
    {
        var m = new Dictionary<string, string>(capacity: 8);
        void put(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) m[key] = value.Trim();
        }
        put("utm_source", request.UtmSource);
        put("utm_medium", request.UtmMedium);
        put("utm_campaign", request.UtmCampaign);
        put("utm_content", request.UtmContent);
        put("utm_term", request.UtmTerm);
        put("gclid", request.Gclid);
        put("fbclid", request.Fbclid);
        put("referrer", request.Referrer);
        return m;
    }

    /// <summary>
    /// Resolução de slug sem TenantId — usa o método dedicado do repository
    /// (<c>GetPublishedBySlugGlobalAsync</c>) que filtra por PUBLISHED.
    /// Single-tenant: slug efetivamente único globalmente. Multi-tenant real
    /// requer subdomínio/host pra disambiguar.
    /// </summary>
    private Task<Product?> FindPublishedBySlugAnyTenantAsync(string slug)
        => productRepository.GetPublishedBySlugGlobalAsync(slug);
}
