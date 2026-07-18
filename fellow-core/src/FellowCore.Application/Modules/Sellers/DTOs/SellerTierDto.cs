using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Sellers.DTOs;

/// <summary>
/// Estado de tier do seller pro portal. Calculado on-the-fly (Sprint 0) a partir
/// de TPV30d (soma de NetAmount das TXs CAPTURED nos últimos 30 dias).
///
/// IMPORTANT (Sprint 0 escopo):
///   - Não persiste, não tem cooldown de descida, não dispara evento de mudança.
///   - Não rebaixa Founding Sellers — Founding é ortogonal a tier.
///   - INFINITE não é atribuído automaticamente: o cálculo automático para em BLACK.
///     Pra colocar um seller em INFINITE, precisa de admin override (Sprint 1).
///
/// Esses limites virarão job + persistência + evento na Sprint 1 conforme backlog.
/// </summary>
public record SellerTierDto(
    /// <summary>Tier atual derivado de <see cref="Tpv30dBrl"/>.</summary>
    SellerTier CurrentTier,
    /// <summary>TPV nos últimos 30 dias (soma de NetAmount das CAPTURED), em reais.</summary>
    decimal Tpv30dBrl,
    /// <summary>Próximo tier alcançável (null quando seller já está em BLACK ou INFINITE).</summary>
    SellerTier? NextTier,
    /// <summary>Quanto falta de TPV30d pra atingir <see cref="NextTier"/> (R$). Null quando NextTier=null.</summary>
    decimal? GapToNextBrl,
    /// <summary>Marca Founding (independente de tier — pode ser Silver Founding #3, etc).</summary>
    bool IsFoundingSeller,
    /// <summary>Ordinal Founding, null quando IsFoundingSeller=false.</summary>
    int? FoundingNumber
);
