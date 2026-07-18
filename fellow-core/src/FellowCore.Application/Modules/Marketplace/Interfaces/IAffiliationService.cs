using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Marketplace.Interfaces;

public interface IAffiliationService
{
    /// <summary>
    /// Afiliado pede afiliação a um produto. Se Product.AffiliationMode = OPEN,
    /// auto-aprova. Caso REQUEST, fica PENDING aguardando produtor.
    /// Caso CLOSED, retorna BusinessException.
    /// </summary>
    Task<AffiliationDto> RequestAsync(Guid tenantId, Guid affiliateSellerId, RequestAffiliationDto request);

    /// <summary>
    /// Produtor aprova afiliação PENDING. Opcionalmente sobrescreve a comissão default.
    /// </summary>
    Task<AffiliationDto> ApproveAsync(Guid tenantId, Guid ownerSellerId, Guid? userId, Guid affiliationId, ApproveAffiliationDto request);

    Task<AffiliationDto> RejectAsync(Guid tenantId, Guid ownerSellerId, Guid? userId, Guid affiliationId, RejectAffiliationDto request);

    Task<AffiliationDto> RevokeAsync(Guid tenantId, Guid ownerSellerId, Guid? userId, Guid affiliationId, RevokeAffiliationDto request);

    /// <summary>
    /// Resolve afiliação por id. Authz: deve ser o próprio afiliado OU o
    /// produtor dono do produto. Outros sellers do tenant recebem null
    /// (mesma resposta de "não encontrado" — evita info leak por enumeration).
    /// </summary>
    Task<AffiliationDto?> GetByIdAsync(Guid tenantId, Guid requesterSellerId, Guid affiliationId);

    Task<AffiliationListDto> ListByProductAsync(
        Guid tenantId, Guid ownerSellerId, Guid productId, AffiliationStatus? status, int page, int pageSize);

    Task<AffiliationListDto> ListBySellerAsync(
        Guid tenantId, Guid affiliateSellerId, AffiliationStatus? status, int page, int pageSize);

    /// <summary>
    /// Resolve uma Affiliation pelo tracking code — usado pelo checkout.
    /// Retorna null se não existe OU se não está APPROVED (tracking de
    /// affiliations PENDING/REJECTED/REVOKED não converte).
    /// </summary>
    Task<AffiliationDto?> ResolveTrackingCodeAsync(string trackingCode);

    /// <summary>
    /// Métricas de performance da afiliação — TPV, vendas e ganhos em 2 janelas
    /// (30d / all-time). Requer ownership: só o próprio afiliado vê stats da
    /// afiliação dele (ou o produtor dono do produto). Retorna null se a
    /// afiliação não existe.
    /// </summary>
    Task<AffiliateStatsDto?> GetStatsAsync(Guid tenantId, Guid requesterSellerId, Guid affiliationId, int days = 30);

    /// <summary>
    /// Top N afiliados de um produto ordenados por TPV. Acessível pelo produtor
    /// dono do produto (gestão dos parceiros) — outros sellers do tenant
    /// recebem 403.
    /// </summary>
    Task<IReadOnlyList<AffiliateLeaderboardEntryDto>> GetLeaderboardAsync(
        Guid tenantId, Guid ownerSellerId, Guid productId, int limit);

    /// <summary>
    /// Stats COMPACTAS (sales30d, clicks30d, earnings30d) de TODAS as afiliações
    /// do seller — usado pra embutir mini-métricas na lista de
    /// /affiliations. Mais barato que chamar `/affiliations/{id}/stats` N vezes:
    /// agrega tudo em ~2 queries (TX splits + clicks por affiliation).
    /// </summary>
    Task<IReadOnlyList<AffiliateMiniStatsDto>> GetMyMiniStatsAsync(
        Guid tenantId, Guid affiliateSellerId);
}
