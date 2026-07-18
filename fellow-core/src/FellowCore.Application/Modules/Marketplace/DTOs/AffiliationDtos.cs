using System.ComponentModel.DataAnnotations;

namespace FellowCore.Application.Modules.Marketplace.DTOs;

public record RequestAffiliationDto(
    [Required] Guid ProductId
);

public record ApproveAffiliationDto(
    [Range(0, 100)] decimal? OverrideCommissionPercent = null
);

public record RejectAffiliationDto(
    [MaxLength(500)] string? Reason = null
);

public record RevokeAffiliationDto(
    [MaxLength(500)] string? Reason = null
);

public record AffiliationDto(
    Guid Id,
    Guid ProductId,
    string? ProductName,
    string? ProductSlug,
    decimal? ProductPrice,
    /// <summary>Cover do produto pra hero visual no dashboard do afiliado.</summary>
    string? ProductCoverImageUrl,
    Guid AffiliateSellerId,
    string? AffiliateSellerName,
    int Status,
    decimal? CommissionPercent,
    decimal EffectiveCommissionPercent,
    string TrackingCode,
    /// <summary>URL completa de checkout com tracking — pronta pra copiar/compartilhar.
    /// Gerada pelo backend usando o slug do produto + tracking code.</summary>
    string CheckoutUrl,
    DateTime RequestedAt,
    DateTime? ApprovedAt,
    DateTime? RejectedAt,
    DateTime? RevokedAt,
    string? RejectedReason,
    DateTime CreatedAt
);

public record AffiliationListDto(IReadOnlyList<AffiliationDto> Items, int TotalCount);

/// <summary>
/// Métricas de performance da afiliação — usadas no dashboard do afiliado.
/// Sempre 2 janelas: 30 dias (recente, ação rápida) + all-time (acumulado total).
/// TPV = total transactioned volume bruto que esta afiliação trouxe pro produto;
/// Earnings = comissão líquida que o afiliado realmente ganhou nessas vendas.
///
/// EarningsPending: comissões já calculadas mas ainda não disponibilizadas
/// (Splits em status PENDING/RESERVED/PROCESSING — ex: TX capturada mas split
/// ainda não distribuído pro SELLER_WALLET, ou esperando settlement delay
/// de cartão D+30).
/// </summary>
/// <summary>
/// Stats compactas de uma afiliação — pra render mini-métricas inline na
/// lista de /affiliations. Sem timeseries / previous / etc., só o essencial.
/// </summary>
public record AffiliateMiniStatsDto(
    Guid AffiliationId,
    int Sales30d,
    int Clicks30d,
    decimal Earnings30d
);

/// <summary>
/// Entry no leaderboard de afiliados de um produto. Ordenado por TPV desc.
/// </summary>
public record AffiliateLeaderboardEntryDto(
    Guid AffiliationId,
    Guid AffiliateSellerId,
    string? AffiliateName,
    int SalesCount,
    decimal Tpv,
    decimal Earnings,
    int Rank
);

public record AffiliateStatsDto(
    Guid AffiliationId,
    Guid ProductId,
    string ProductName,
    // Tamanho da janela configurável (7/30/90 dias)
    int PeriodDays,
    // Métricas na janela atual
    int SalesInPeriod,
    decimal TpvInPeriod,
    decimal EarningsInPeriod,
    // All-time (desde a aprovação)
    int SalesAllTime,
    decimal TpvAllTime,
    decimal EarningsAllTime,
    // Saldo a receber (splits ainda não liberados)
    decimal EarningsPending,
    // Tracking de cliques no link de divulgação (dedup ~1h por fingerprint)
    int ClicksInPeriod,
    int ClicksAllTime,
    // Conversão: vendas / clicks * 100. Null quando clicks == 0 (sem dado pra
    // calcular — comunicar "sem dados" é melhor que "0%" enganoso).
    decimal? ConversionPercentInPeriod,
    decimal? ConversionPercentAllTime,
    // Timeseries — arrays de `PeriodDays` ints. Sparkline no front.
    int[] ClicksByDay,
    int[] SalesByDay,
    // Período anterior (mesma duração imediatamente antes) — pra delta badges.
    int PreviousSalesInPeriod,
    decimal PreviousTpvInPeriod,
    decimal PreviousEarningsInPeriod,
    int PreviousClicksInPeriod
);
