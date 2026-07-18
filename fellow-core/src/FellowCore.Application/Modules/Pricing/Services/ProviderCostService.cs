using FellowCore.Application.Common;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Pricing.Services;

public class ProviderCostService(
    IProviderCostScheduleRepository repository,
    IConfiguration configuration,
    IAppMetrics appMetrics,
    ILogger<ProviderCostService> logger) : IProviderCostService
{
    private bool IsProduction => string.Equals(
        configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["Environment"],
        "Production", StringComparison.OrdinalIgnoreCase);

    public async Task<decimal> CalculateProviderCostAsync(
        PaymentProvider provider,
        PaymentType paymentType,
        decimal amount)
    {
        var schedule = await repository.GetAsync(provider, paymentType);

        if (schedule is null)
        {
            if (IsProduction)
            {
                logger.LogCritical(
                    "[COST] No ProviderCostSchedule for {Provider}/{PaymentType} in PRODUCTION. " +
                    "Margin breakdown will be null for this transaction. Add schedule immediately.",
                    provider, paymentType);
                appMetrics.RecordProviderError(provider.ToString(), "missing_cost_schedule");

                // Throw so caller leaves margin fields null (avoids inflated margin with cost=0)
                throw new InvalidOperationException(
                    $"No ProviderCostSchedule for {provider}/{paymentType}. Cannot calculate cost in production.");
            }

            logger.LogDebug(
                "No ProviderCostSchedule found for {Provider}/{PaymentType}. Returning 0 (dev/sandbox)",
                provider, paymentType);
            return 0m;
        }

        var rawCost = amount * schedule.PercentFee / 100m + schedule.FixedFee;
        var maxFee = schedule.MaxFee ?? decimal.MaxValue;
        var cost = Math.Max(schedule.MinFee, Math.Min(maxFee, rawCost));

        return RoundingPolicy.Round(cost);
    }

    public async Task<decimal> CalculateProviderCostWithInstallmentsAsync(
        PaymentProvider provider,
        PaymentType paymentType,
        decimal amount,
        int installments)
    {
        // 1x reusa o caminho base — sem surcharge.
        var baseCost = await CalculateProviderCostAsync(provider, paymentType, amount);
        if (installments <= 1) return baseCost;

        var schedule = await repository.GetAsync(provider, paymentType);
        if (schedule is null) return baseCost; // sem schedule, sem surcharge

        // Surcharge é % sobre o amount gross. Modelo Stripe BR: 3x → +3.5% etc.
        var surchargePct = schedule.GetInstallmentSurchargePercent(installments);
        if (surchargePct <= 0) return baseCost;

        var surcharge = amount * surchargePct / 100m;
        return RoundingPolicy.Round(baseCost + surcharge);
    }
}
