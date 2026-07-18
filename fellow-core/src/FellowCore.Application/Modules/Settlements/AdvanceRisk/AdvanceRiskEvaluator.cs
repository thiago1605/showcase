using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options; // requires Microsoft.Extensions.Options package — added below

namespace FellowCore.Application.Modules.Settlements.AdvanceRisk;

/// <summary>
/// Configuração das regras anti-fraude de ADVANCE. Default conservador.
/// Ajustável via appsettings (seção "AdvanceRisk") ou per-tenant no futuro.
/// </summary>
public sealed class AdvanceRiskOptions
{
    public int MinSellerAgeDays { get; set; } = 30;
    public int NewSellerThresholdDays { get; set; } = 90;
    public decimal NewSellerMaxAmount { get; set; } = 5_000m;
    public double MaxRiskScore { get; set; } = 0.7;
    public decimal MaxChargebackRate { get; set; } = 0.01m;       // 1%
    public int ChargebackLookbackDays { get; set; } = 90;
    public int ChargebackMinSampleSize { get; set; } = 10;        // ignora rate se < N TXs
}

/// <summary>
/// Implementação MVP: regras codificadas, evaluating in-order.
/// Falha rápido (first block wins) — não acumula sinais. Pra observabilidade
/// detalhada, expandir pra fluent rules + cobertura per-rule.
/// </summary>
public class AdvanceRiskEvaluator(
    ITransactionRepository transactionRepository,
    ISellerRiskProfileRepository riskProfileRepository,
    IOptions<AdvanceRiskOptions> options,
    TimeProvider timeProvider,
    ILogger<AdvanceRiskEvaluator> logger) : IAdvanceRiskEvaluator
{
    private readonly AdvanceRiskOptions _opt = options.Value;

    /// <summary>Threshold de staleness — profile mais velho que isso é re-calculado on-demand.</summary>
    private static readonly TimeSpan ProfileStalenessThreshold = TimeSpan.FromHours(48);

    public async Task<AdvanceRiskEvaluation> EvaluateAsync(Transaction tx, Seller seller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(seller);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // R1: Seller ACTIVE + KYC (provider account vinculado)
        if (seller.Status != SellerStatus.ACTIVE)
            return AdvanceRiskEvaluation.Block("seller_not_active", $"status={seller.Status}");

        if (string.IsNullOrEmpty(seller.ExternalAccountId))
            return AdvanceRiskEvaluation.Block("seller_no_connect_account");

        // R2: Idade mínima
        var sellerAge = now - seller.CreatedAt;
        if (sellerAge < TimeSpan.FromDays(_opt.MinSellerAgeDays))
            return AdvanceRiskEvaluation.Block("seller_too_new",
                $"age_days={(int)sellerAge.TotalDays}",
                $"min_required={_opt.MinSellerAgeDays}");

        // R3: Cap de valor pra seller novo (entre min e threshold)
        if (sellerAge < TimeSpan.FromDays(_opt.NewSellerThresholdDays) && tx.Amount > _opt.NewSellerMaxAmount)
            return AdvanceRiskEvaluation.Block("amount_over_cap_for_new_seller",
                $"amount={tx.Amount}",
                $"cap={_opt.NewSellerMaxAmount}",
                $"age_days={(int)sellerAge.TotalDays}");

        // R4: RiskScore da TX (se setado pelo antifraude externo)
        if (tx.RiskScore.HasValue && tx.RiskScore.Value > _opt.MaxRiskScore)
            return AdvanceRiskEvaluation.Block("high_risk_score",
                $"score={tx.RiskScore.Value:F2}",
                $"threshold={_opt.MaxRiskScore}");

        // R5: Chargeback rate histórica — prioriza profile materializado (1 SELECT por PK).
        // Fallback pra computação on-demand quando profile não existe ou está stale (>48h
        // indica que o refresh job falhou; melhor pagar latência extra que decidir com dado velho).
        decimal chargebackRate;
        int totalCaptured;
        int chargebackLost;

        var profile = await riskProfileRepository.GetBySellerIdAsync(seller.Id);
        if (profile != null && !profile.IsStale(ProfileStalenessThreshold, now))
        {
            chargebackRate = profile.ChargebackRate;
            totalCaptured = profile.CapturedCount90d;
            chargebackLost = profile.ChargebackLostCount90d;
        }
        else
        {
            var since = now.AddDays(-_opt.ChargebackLookbackDays);
            var stats = await transactionRepository.GetSellerRiskStatsAsync(seller.Id, since);
            chargebackRate = stats.ChargebackRate;
            totalCaptured = stats.TotalCapturedCount;
            chargebackLost = stats.ChargebackLostCount;
            logger.LogDebug(
                "[ADVANCE_RISK] TX {Id} usou fallback on-demand (profile {State})",
                tx.Id, profile == null ? "missing" : "stale");
        }

        if (totalCaptured >= _opt.ChargebackMinSampleSize && chargebackRate > _opt.MaxChargebackRate)
        {
            return AdvanceRiskEvaluation.Block("high_chargeback_rate",
                $"rate={chargebackRate:P2}",
                $"captured={totalCaptured}",
                $"lost={chargebackLost}",
                $"threshold={_opt.MaxChargebackRate:P2}");
        }

        logger.LogDebug(
            "[ADVANCE_RISK] TX {Id} elegível: sellerAge={AgeD}d, riskScore={Score}, cbRate={Rate}",
            tx.Id, (int)sellerAge.TotalDays, tx.RiskScore, chargebackRate);

        return AdvanceRiskEvaluation.Eligible();
    }
}
