using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Sellers.DTOs;
using FellowCore.Application.Modules.Sellers.Interfaces;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.Sellers.Services;

/// <summary>
/// Sprint 1: serviço de leitura do tier do seller. Estratégia híbrida:
///   1. Se houver <c>SellerTierProfile</c> persistido (job mensal rodou), usa ele.
///      Retorna o tier oficial, o TPV90d snapshot e o status de freeze.
///   2. Caso contrário (seller novo OU job nunca rodou), fallback pro cálculo
///      on-the-fly a partir de TPV30d (Sprint 0 behavior). Não persiste —
///      a próxima execução do job materializa.
///
/// O endpoint público sempre retorna <see cref="SellerTierDto"/> consistente —
/// frontend não precisa saber se veio do banco ou foi calculado on-the-fly.
/// </summary>
public class SellerTierService(
    ISellerRepository sellerRepository,
    ISellerTierProfileRepository tierProfileRepository,
    TimeProvider timeProvider) : ISellerTierService
{
    // Limites em R$ (rolling window). Fonte: docs/pricing_policy.md (Sprint 0).
    // INFINITE não aparece aqui — só atribuível via admin override (Sprint 2).
    private static readonly (SellerTier Tier, decimal Floor, decimal Ceiling)[] Thresholds =
    [
        (SellerTier.SILVER,   0m,            50_000m),
        (SellerTier.GOLD,     50_000m,       250_000m),
        (SellerTier.DIAMOND,  250_000m,      1_000_000m),
        (SellerTier.BLACK,    1_000_000m,    decimal.MaxValue),
    ];

    public async Task<SellerTierDto> GetTierAsync(Guid tenantId, Guid sellerId)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} nao encontrado.");

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Tenta profile persistido (oficial — atualizado pelo job mensal).
        var profile = await tierProfileRepository.GetBySellerIdAsync(tenantId, sellerId);
        if (profile != null)
        {
            var (_, nextTierFromProfile, gapFromProfile) = Resolve(profile.Tpv90dSnapshotBrl);
            return new SellerTierDto(
                CurrentTier: profile.Tier,
                Tpv30dBrl: profile.Tpv90dSnapshotBrl, // snapshot do que o job viu (TPV90d, não 30d)
                NextTier: nextTierFromProfile,
                GapToNextBrl: gapFromProfile,
                IsFoundingSeller: seller.IsFoundingSeller,
                FoundingNumber: seller.FoundingNumber);
        }

        // Fallback: seller novo ou job nunca rodou — calcula on-the-fly por TPV30d.
        var since = now.AddDays(-30);
        decimal tpv30d = await sellerRepository.GetCapturedNetSumAsync(tenantId, sellerId, since);

        var (currentTier, nextTier, gap) = Resolve(tpv30d);

        return new SellerTierDto(
            CurrentTier: currentTier,
            Tpv30dBrl: tpv30d,
            NextTier: nextTier,
            GapToNextBrl: gap,
            IsFoundingSeller: seller.IsFoundingSeller,
            FoundingNumber: seller.FoundingNumber);
    }

    /// <summary>
    /// Resolve tier + next + gap pra um TPV. Não considera INFINITE
    /// (só atribuível via admin override). Public + static pra testar
    /// sem mockar repositório/clock — função pura.
    /// </summary>
    public static (SellerTier CurrentTier, SellerTier? NextTier, decimal? Gap) Resolve(decimal tpv30d)
    {
        for (int i = 0; i < Thresholds.Length; i++)
        {
            var (tier, floor, ceiling) = Thresholds[i];
            if (tpv30d >= floor && tpv30d < ceiling)
            {
                if (i + 1 < Thresholds.Length)
                {
                    var (nextTier, nextFloor, _) = Thresholds[i + 1];
                    return (tier, nextTier, nextFloor - tpv30d);
                }
                return (tier, null, null); // já está em BLACK (topo automático)
            }
        }
        // Não deveria cair aqui — TPV negativo seria bug. Defesa: trata como SILVER.
        return (SellerTier.SILVER, SellerTier.GOLD, Thresholds[1].Floor);
    }
}
