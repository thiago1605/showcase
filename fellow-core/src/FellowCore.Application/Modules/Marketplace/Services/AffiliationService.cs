using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using FellowCore.Application.Modules.Notifications.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;

namespace FellowCore.Application.Modules.Marketplace.Services;

public class AffiliationService(
    IAffiliationRepository affiliationRepository,
    IProductRepository productRepository,
    ISellerRepository sellerRepository,
    INotificationService notificationService,
    IConfiguration configuration
) : IAffiliationService
{
    /// <summary>
    /// Base URL pro checkout público — usada pra montar a CheckoutUrl no DTO.
    /// Configurável via Checkout:PublicBaseUrl (ex: https://pay.fellowpay.com.br).
    /// Fallback: vazio → frontend monta com sua origin.
    /// </summary>
    private string CheckoutBaseUrl =>
        configuration["Checkout:PublicBaseUrl"]?.TrimEnd('/') ?? string.Empty;

    public async Task<AffiliationDto> RequestAsync(Guid tenantId, Guid affiliateSellerId, RequestAffiliationDto request)
    {
        var product = await productRepository.GetByIdAsync(tenantId, request.ProductId)
            ?? throw new NotFoundException("Product.NotFound", $"Produto {request.ProductId} não encontrado.");

        if (product.OwnerSellerId == affiliateSellerId)
            throw new BusinessException("Affiliation.SelfRequest",
                "Você não pode se afiliar ao seu próprio produto.");

        if (!product.IsOpenForAffiliation)
            throw new BusinessException("Affiliation.Closed",
                "Este produto não está aceitando afiliações no momento.");

        // Duplicate guard antes de inserir (partial unique index pega o resto,
        // mas mensagem amigável aqui). Traduzimos o enum para linguagem natural —
        // o caller é UI/usuário final, não deve ver "PENDING"/"APPROVED" crus.
        var existing = await affiliationRepository.GetActiveByProductAndSellerAsync(
            product.Id, affiliateSellerId);
        if (existing is not null)
        {
            var statusLabel = existing.Status switch
            {
                AffiliationStatus.PENDING =>
                    "Sua solicitação para este produto já foi enviada e está aguardando aprovação do produtor.",
                AffiliationStatus.APPROVED =>
                    "Você já tem uma afiliação ativa para este produto. Acesse seu link em \"Minhas afiliações\".",
                AffiliationStatus.REJECTED =>
                    "Sua solicitação anterior para este produto foi recusada pelo produtor.",
                AffiliationStatus.REVOKED =>
                    "Sua afiliação a este produto foi revogada. Entre em contato com o produtor.",
                _ => "Você já tem uma afiliação registrada para este produto.",
            };
            throw new BusinessException("Affiliation.AlreadyExists", statusLabel);
        }

        var affiliation = Affiliation.Create(
            tenantId: tenantId,
            productId: product.Id,
            affiliateSellerId: affiliateSellerId,
            autoApprove: product.IsAutoApproveAffiliation);

        affiliationRepository.Add(affiliation);
        await affiliationRepository.SaveChangesAsync();

        // Notifica o produtor — se PENDING, urge a aprovação; se já APPROVED
        // (modo OPEN), confirma que o afiliado entrou. Notifications são
        // outbox-based: vão na mesma TX do SaveChanges acima... opa, não
        // exatamente — SaveChanges já rodou. Como NotificationService.CreateAsync
        // só faz Add no outbox, precisa de outro SaveChanges. Repo do
        // notification outbox tem seu próprio SaveChanges interno via worker.
        // Mas como producer interno faz Add sem SaveChanges, precisamos
        // disparar SaveChanges aqui de novo OU mover o producer pra ANTES do
        // SaveChanges da afiliação. Optei por disparar segundo SaveChanges
        // via notification — outboxRepo.SaveChanges interno cobre.
        await NotifyProducerAsync(tenantId, product, affiliation);

        return await ToDtoAsync(affiliation, product, await GetAffiliateSellerNameAsync(tenantId, affiliateSellerId));
    }

    public async Task<AffiliationDto> ApproveAsync(
        Guid tenantId, Guid ownerSellerId, Guid? userId, Guid affiliationId, ApproveAffiliationDto request)
    {
        var (affiliation, product) = await LoadAndAuthorizeAsync(tenantId, ownerSellerId, affiliationId);

        try { affiliation.Approve(userId, request.OverrideCommissionPercent); }
        catch (InvalidOperationException ex) { throw new BusinessException("Affiliation.InvalidTransition", ex.Message); }

        affiliationRepository.Update(affiliation);
        await affiliationRepository.SaveChangesAsync();

        // Notifica o afiliado que foi aprovado.
        await notificationService.NotifyAffiliationApprovedAsync(
            tenantId, affiliation.AffiliateSellerId, affiliation.Id, product.Name);

        return await ToDtoAsync(affiliation, product, await GetAffiliateSellerNameAsync(tenantId, affiliation.AffiliateSellerId));
    }

    public async Task<AffiliationDto> RejectAsync(
        Guid tenantId, Guid ownerSellerId, Guid? userId, Guid affiliationId, RejectAffiliationDto request)
    {
        var (affiliation, product) = await LoadAndAuthorizeAsync(tenantId, ownerSellerId, affiliationId);

        try { affiliation.Reject(userId, request.Reason); }
        catch (InvalidOperationException ex) { throw new BusinessException("Affiliation.InvalidTransition", ex.Message); }

        affiliationRepository.Update(affiliation);
        await affiliationRepository.SaveChangesAsync();

        await notificationService.NotifyAffiliationRejectedAsync(
            tenantId, affiliation.AffiliateSellerId, affiliation.Id, product.Name, request.Reason);

        return await ToDtoAsync(affiliation, product, await GetAffiliateSellerNameAsync(tenantId, affiliation.AffiliateSellerId));
    }

    public async Task<AffiliationDto> RevokeAsync(
        Guid tenantId, Guid ownerSellerId, Guid? userId, Guid affiliationId, RevokeAffiliationDto request)
    {
        var (affiliation, product) = await LoadAndAuthorizeAsync(tenantId, ownerSellerId, affiliationId);

        try { affiliation.Revoke(userId, request.Reason); }
        catch (InvalidOperationException ex) { throw new BusinessException("Affiliation.InvalidTransition", ex.Message); }

        affiliationRepository.Update(affiliation);
        await affiliationRepository.SaveChangesAsync();

        return await ToDtoAsync(affiliation, product, await GetAffiliateSellerNameAsync(tenantId, affiliation.AffiliateSellerId));
    }

    public async Task<AffiliationDto?> GetByIdAsync(Guid tenantId, Guid requesterSellerId, Guid affiliationId)
    {
        var a = await affiliationRepository.GetByIdAsync(tenantId, affiliationId);
        if (a is null) return null;

        var product = a.Product ?? await productRepository.GetByIdAsync(tenantId, a.ProductId);

        // Authz: só afiliado dono OU produtor do produto vê a afiliação.
        // Outros sellers do tenant recebem null (= 404 no controller) — não
        // expomos a existência via diferenciar 403 vs 404, contra enumeration.
        var isAffiliate = a.AffiliateSellerId == requesterSellerId;
        var isProducer = product is not null && product.OwnerSellerId == requesterSellerId;
        if (!isAffiliate && !isProducer) return null;

        return await ToDtoAsync(a, product, await GetAffiliateSellerNameAsync(tenantId, a.AffiliateSellerId));
    }

    public async Task<AffiliationListDto> ListByProductAsync(
        Guid tenantId, Guid ownerSellerId, Guid productId, AffiliationStatus? status, int page, int pageSize)
    {
        // Authz: só o owner do produto pode listar afiliações dele.
        var product = await productRepository.GetByIdAsync(tenantId, productId)
            ?? throw new NotFoundException("Product.NotFound", $"Produto {productId} não encontrado.");
        if (product.OwnerSellerId != ownerSellerId)
            throw new BusinessException("Affiliation.NotProductOwner",
                "Apenas o produtor pode listar afiliações deste produto.");

        var (skip, take) = Paginate(page, pageSize);
        var (items, total) = await affiliationRepository.GetByProductAsync(tenantId, productId, status, skip, take);
        var dtos = new List<AffiliationDto>(items.Count);
        foreach (var a in items)
        {
            var name = a.AffiliateSeller?.TradeName ?? a.AffiliateSeller?.LegalName
                ?? await GetAffiliateSellerNameAsync(tenantId, a.AffiliateSellerId);
            dtos.Add(await ToDtoAsync(a, product, name));
        }
        return new AffiliationListDto(dtos, total);
    }

    public async Task<AffiliationListDto> ListBySellerAsync(
        Guid tenantId, Guid affiliateSellerId, AffiliationStatus? status, int page, int pageSize)
    {
        var (skip, take) = Paginate(page, pageSize);
        var (items, total) = await affiliationRepository.GetBySellerAsync(tenantId, affiliateSellerId, status, skip, take);
        var dtos = new List<AffiliationDto>(items.Count);
        foreach (var a in items)
        {
            var name = await GetAffiliateSellerNameAsync(tenantId, a.AffiliateSellerId);
            dtos.Add(await ToDtoAsync(a, a.Product, name));
        }
        return new AffiliationListDto(dtos, total);
    }

    public async Task<AffiliationDto?> ResolveTrackingCodeAsync(string trackingCode)
    {
        if (string.IsNullOrWhiteSpace(trackingCode)) return null;
        var a = await affiliationRepository.GetByTrackingCodeAsync(trackingCode.Trim());
        if (a is null) return null;
        if (!a.IsActive) return null; // só APPROVED converte
        var name = await GetAffiliateSellerNameAsync(a.TenantId, a.AffiliateSellerId);
        return await ToDtoAsync(a, a.Product, name);
    }

    public async Task<IReadOnlyList<AffiliateMiniStatsDto>> GetMyMiniStatsAsync(
        Guid tenantId, Guid affiliateSellerId)
    {
        // Lista TODAS as afiliações do seller (até 100 — limite generoso pra
        // afiliado individual; raramente passa). Não precisa de pagination
        // pq esse endpoint é pra enriquecer a lista que o front já paginou.
        var (affs, _) = await affiliationRepository.GetBySellerAsync(
            tenantId, affiliateSellerId, status: null, skip: 0, take: 100);

        // Pra cada afiliação, chama GetAffiliateMetricsAsync — sim, é N queries,
        // mas com cache de query plan e índices é rápido (afiliado típico tem
        // 5-15 afiliações). Se virar gargalo, vira batch query no repo.
        var result = new List<AffiliateMiniStatsDto>(affs.Count);
        foreach (var aff in affs)
        {
            // Mini-stats sempre 30d — não exposto na UI como configurável.
            var snap = await affiliationRepository.GetAffiliateMetricsAsync(
                tenantId, aff.AffiliateSellerId, aff.ProductId, aff.Id, days: 30);
            result.Add(new AffiliateMiniStatsDto(
                AffiliationId: aff.Id,
                Sales30d: snap.SalesInPeriod,
                Clicks30d: snap.ClicksInPeriod,
                Earnings30d: snap.EarningsInPeriod));
        }
        return result;
    }

    public async Task<IReadOnlyList<AffiliateLeaderboardEntryDto>> GetLeaderboardAsync(
        Guid tenantId, Guid ownerSellerId, Guid productId, int limit)
    {
        var product = await productRepository.GetByIdAsync(tenantId, productId)
            ?? throw new NotFoundException("Product.NotFound", $"Produto {productId} não encontrado.");
        if (product.OwnerSellerId != ownerSellerId)
            throw new BusinessException("Product.NotOwner",
                "Apenas o produtor pode ver o leaderboard.");

        var entries = await affiliationRepository.GetLeaderboardAsync(tenantId, productId, limit);
        return entries.Select((e, idx) => new AffiliateLeaderboardEntryDto(
            AffiliationId: e.AffiliationId,
            AffiliateSellerId: e.AffiliateSellerId,
            AffiliateName: e.AffiliateName,
            SalesCount: e.SalesCount,
            Tpv: e.Tpv,
            Earnings: e.Earnings,
            Rank: idx + 1)).ToList();
    }

    public async Task<AffiliateStatsDto?> GetStatsAsync(
        Guid tenantId, Guid requesterSellerId, Guid affiliationId, int days = 30)
    {
        // Range 1..365 dias — afrouxado vs whitelist 7/30/90 antiga pra
        // permitir que o seller escolha qualquer janela. Cap em 365 evita
        // abuso (queries pesadas + arrays de timeseries gigantes). Inválidos
        // caem pra 30 (default razoável).
        if (days < 1 || days > 365) days = 30;

        var aff = await affiliationRepository.GetByIdAsync(tenantId, affiliationId);
        if (aff is null) return null;

        // Authz: o afiliado pode ver suas próprias stats. O produtor (dono do
        // produto) também — pra avaliar performance do afiliado antes de
        // approve futuro / leaderboard. Outros sellers do tenant: bloqueado.
        var product = aff.Product ?? await productRepository.GetByIdAsync(tenantId, aff.ProductId);
        if (product is null) return null;
        var isAffiliate = aff.AffiliateSellerId == requesterSellerId;
        var isProducer = product.OwnerSellerId == requesterSellerId;
        if (!isAffiliate && !isProducer)
            throw new BusinessException("Affiliation.NotAuthorized",
                "Você não tem permissão para ver as métricas dessa afiliação.");

        var snap = await affiliationRepository.GetAffiliateMetricsAsync(
            tenantId, aff.AffiliateSellerId, aff.ProductId, aff.Id, days);

        // Conversão = vendas / clicks. Define como null se não houve clicks
        // (evita divisão por zero + comunica "sem dados" vs "0%").
        decimal? conversionInPeriod = snap.ClicksInPeriod > 0
            ? Math.Round((decimal)snap.SalesInPeriod / snap.ClicksInPeriod * 100m, 2)
            : (decimal?)null;
        decimal? conversionAllTime = snap.ClicksAllTime > 0
            ? Math.Round((decimal)snap.SalesAllTime / snap.ClicksAllTime * 100m, 2)
            : (decimal?)null;

        return new AffiliateStatsDto(
            AffiliationId: aff.Id,
            ProductId: aff.ProductId,
            ProductName: product.Name,
            PeriodDays: snap.PeriodDays,
            SalesInPeriod: snap.SalesInPeriod,
            TpvInPeriod: snap.TpvInPeriod,
            EarningsInPeriod: snap.EarningsInPeriod,
            SalesAllTime: snap.SalesAllTime,
            TpvAllTime: snap.TpvAllTime,
            EarningsAllTime: snap.EarningsAllTime,
            EarningsPending: snap.EarningsPending,
            ClicksInPeriod: snap.ClicksInPeriod,
            ClicksAllTime: snap.ClicksAllTime,
            ConversionPercentInPeriod: conversionInPeriod,
            ConversionPercentAllTime: conversionAllTime,
            ClicksByDay: snap.ClicksByDay,
            SalesByDay: snap.SalesByDay,
            PreviousSalesInPeriod: snap.PreviousSalesInPeriod,
            PreviousTpvInPeriod: snap.PreviousTpvInPeriod,
            PreviousEarningsInPeriod: snap.PreviousEarningsInPeriod,
            PreviousClicksInPeriod: snap.PreviousClicksInPeriod);
    }

    // --- internals ---

    private async Task<(Affiliation, Product)> LoadAndAuthorizeAsync(Guid tenantId, Guid ownerSellerId, Guid affiliationId)
    {
        var affiliation = await affiliationRepository.GetByIdAsync(tenantId, affiliationId)
            ?? throw new NotFoundException("Affiliation.NotFound", $"Afiliação {affiliationId} não encontrada.");
        var product = affiliation.Product ?? await productRepository.GetByIdAsync(tenantId, affiliation.ProductId);
        if (product is null)
            throw new NotFoundException("Product.NotFound", $"Produto {affiliation.ProductId} não encontrado.");
        if (product.OwnerSellerId != ownerSellerId)
            throw new BusinessException("Affiliation.NotProductOwner",
                "Apenas o produtor pode gerenciar esta afiliação.");
        return (affiliation, product);
    }

    private async Task<string?> GetAffiliateSellerNameAsync(Guid tenantId, Guid sellerId)
    {
        var s = await sellerRepository.GetByIdAsync(tenantId, sellerId);
        return s?.TradeName ?? s?.LegalName;
    }

    private async Task NotifyProducerAsync(Guid tenantId, Product product, Affiliation affiliation)
    {
        // Notification só faz sentido em REQUEST mode — em OPEN o afiliado já
        // foi aprovado, não precisa pedir atenção. Em REQUEST, produtor precisa
        // entrar e aprovar.
        if (affiliation.Status != AffiliationStatus.PENDING) return;

        var affiliateName = await GetAffiliateSellerNameAsync(tenantId, affiliation.AffiliateSellerId)
                            ?? "Um afiliado";
        await notificationService.NotifyAffiliationRequestedAsync(
            tenantId, product.OwnerSellerId, affiliation.Id, product.Name, affiliateName);
    }

    private static (int Skip, int Take) Paginate(int page, int pageSize)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;
        return ((page - 1) * pageSize, pageSize);
    }

    private Task<AffiliationDto> ToDtoAsync(Affiliation a, Product? product, string? affiliateName)
    {
        var productPrice = product?.Price;
        var defaultPct = product?.DefaultAffiliateCommissionPercent ?? 0m;
        var effective = a.EffectiveCommissionPercent(defaultPct);
        var checkoutUrl = product is null
            ? string.Empty
            : $"{CheckoutBaseUrl}/p/{product.Slug}?aff={a.TrackingCode}";

        return Task.FromResult(new AffiliationDto(
            Id: a.Id,
            ProductId: a.ProductId,
            ProductName: product?.Name,
            ProductSlug: product?.Slug,
            ProductPrice: productPrice,
            ProductCoverImageUrl: product?.CoverImageUrl,
            AffiliateSellerId: a.AffiliateSellerId,
            AffiliateSellerName: affiliateName,
            Status: (int)a.Status,
            CommissionPercent: a.CommissionPercent,
            EffectiveCommissionPercent: effective,
            TrackingCode: a.TrackingCode,
            CheckoutUrl: checkoutUrl,
            RequestedAt: a.RequestedAt,
            ApprovedAt: a.ApprovedAt,
            RejectedAt: a.RejectedAt,
            RevokedAt: a.RevokedAt,
            RejectedReason: a.RejectedReason,
            CreatedAt: a.CreatedAt));
    }
}
