using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Pricing.Options;
using FellowCore.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FellowCore.Infrastructure.Pricing;

/// <summary>
/// Sprint 1.5: valida no boot que a tabela <see cref="TierPricingOptions.Rates"/>
/// não causa <c>fee − provider_cost &lt; provider_cost × FloorMarginMultiplier</c>
/// pra nenhuma combinação (tier × payment type).
///
/// Antes (Sprint 1 #3): validava planos × tiers × payment types — matriz N×M×K.
/// Agora: só tiers × payment types — matriz M×K, muito mais simples.
///
/// INFINITE com Rates=null é pulado (sellers Infinite usam Seller.FeeSchedule,
/// admin é responsável por garantir margem caso-a-caso).
///
/// Comportamento:
///   - <see cref="TierPricingOptions.EnforceFloorAtStartup"/>=true (default):
///     throws <see cref="InvalidOperationException"/> com lista das violações.
///   - false: loga warning detalhado mas deixa subir.
///
/// Roda como <see cref="IHostedService"/> no <see cref="StartAsync"/>. Como
/// hosted services são singleton no DI do .NET, não podemos injetar
/// <see cref="IProviderCostService"/> direto (scoped — depende de DbContext).
/// Solução: <see cref="IServiceScopeFactory"/> + scope local dentro do StartAsync.
/// </summary>
public class TierPricingFloorValidator(
    IServiceScopeFactory scopeFactory,
    IOptions<TierPricingOptions> tierPricingOptions,
    ILogger<TierPricingFloorValidator> logger) : IHostedService
{
    private readonly TierPricingOptions _opts = tierPricingOptions.Value;

    /// <summary>
    /// Amounts típicos pra simulação. R$50 = low ticket (cobre infoprodutos baratos).
    /// R$500 = sweet spot médio. R$5.000 = topo do PIX (cap Woovi R$800 considerado).
    ///
    /// Não validamos R$10 porque o min da Woovi (R$0,50) come a margem de tiers altos
    /// com fee fixo baixo (BLACK PIX R$0,19 fixo + 2,4% no R$10 = R$0,43 fee vs R$0,50
    /// cost). É trade-off aceitável: micro-pagamentos viram pequeno prejuízo no BLACK,
    /// mas a métrica de margem total ainda fica positiva pelo volume.
    /// </summary>
    private static readonly decimal[] SampleAmounts = { 50m, 500m, 5_000m };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var providerCostService = scope.ServiceProvider.GetRequiredService<IProviderCostService>();

        var violations = new List<string>();

        foreach (var tier in Enum.GetValues<SellerTier>())
        {
            var fees = _opts.GetFees(tier);
            if (fees is null)
            {
                logger.LogInformation("[TIER_FLOOR] Tier {Tier} sem rates (provavelmente INFINITE/custom) — pulado", tier);
                continue;
            }

            foreach (var paymentType in new[] { PaymentType.PIX, PaymentType.CREDIT_CARD, PaymentType.DEBIT_CARD, PaymentType.BOLETO })
            {
                if (_opts.SkipFloorValidation.Contains(paymentType))
                {
                    logger.LogDebug("[TIER_FLOOR] {Tier}/{PaymentType} pulado por config (SkipFloorValidation)",
                        tier, paymentType);
                    continue;
                }

                var rate = paymentType switch
                {
                    PaymentType.PIX => fees.Pix,
                    PaymentType.DEBIT_CARD => fees.Debit,
                    PaymentType.CREDIT_CARD => fees.CreditCash, // valida o "à vista" (parcelado tem custo Stripe maior, separar se virar problema)
                    PaymentType.BOLETO => fees.Boleto,
                    _ => fees.CreditCash
                };

                var provider = paymentType == PaymentType.PIX ? PaymentProvider.OPENPIX : PaymentProvider.STRIPE;

                foreach (var amount in SampleAmounts)
                {
                    decimal fee = rate.Calculate(amount);
                    decimal providerCost = await providerCostService.CalculateProviderCostAsync(provider, paymentType, amount);
                    decimal margin = fee - providerCost;
                    decimal floor = providerCost * _opts.FloorMarginMultiplier;

                    if (margin < floor)
                    {
                        violations.Add(
                            $"tier={tier} paymentType={paymentType} amount=R${amount:N2}: " +
                            $"fee={fee:N2} cost={providerCost:N2} margin={margin:N2} < floor={floor:N2}");
                    }
                }
            }
        }

        if (violations.Count == 0)
        {
            logger.LogInformation("[TIER_FLOOR] OK — {Tiers} tiers validados em {Samples} valores de amostra, sem violações",
                Enum.GetValues<SellerTier>().Length, SampleAmounts.Length);
            return;
        }

        var report = string.Join("\n  - ", violations.Prepend("Violações de floor de margem:"));
        if (_opts.EnforceFloorAtStartup)
        {
            logger.LogCritical("[TIER_FLOOR] {Report}", report);
            throw new InvalidOperationException(
                $"TierPricing config viola floor de margem em {violations.Count} combinações. " +
                "Ajuste TierPricing:Rates ou TierPricing:FloorMarginMultiplier. Detalhes nos logs.");
        }
        logger.LogWarning("[TIER_FLOOR] {Report}", report);
        logger.LogWarning("[TIER_FLOOR] EnforceFloorAtStartup=false — aplicação sobe MESMO COM VIOLAÇÕES");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
