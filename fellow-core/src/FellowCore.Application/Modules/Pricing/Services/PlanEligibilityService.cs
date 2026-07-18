using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Pricing.Services;

public class PlanEligibilityService(
    ISellerRepository sellerRepository,
    ITransactionRepository transactionRepository,
    IPricingPlanRepository pricingPlanRepository,
    ILogger<PlanEligibilityService> logger) : IPlanEligibilityService
{
    private const decimal CrescaVolumeThreshold = 5_000m;
    private const int CrescaTransactionCountThreshold = 50;
    private const decimal ScalaVolumeThreshold = 100_000m;
    private const int LookbackDays = 30;

    // Plan hierarchy: COMECE < CRESCA < SCALA
    private static readonly string[] PlanHierarchy = ["COMECE", "CRESCA", "SCALA"];

    public async Task<PlanEligibilityResult> CheckEligibilityAsync(Guid tenantId, Guid sellerId)
    {
        var seller = await sellerRepository.GetByIdWithPricingPlanAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} nao encontrado.");

        var currentPlanCode = seller.PricingPlan?.Code ?? "COMECE";
        var currentRank = GetPlanRank(currentPlanCode);

        // Already on the highest plan
        if (currentRank >= PlanHierarchy.Length - 1)
        {
            return new PlanEligibilityResult(
                currentPlanCode,
                EligiblePlanCode: null,
                Reason: "Seller ja esta no plano mais alto (SCALA).",
                CanUpgrade: false);
        }

        var since = DateTime.UtcNow.AddDays(-LookbackDays);
        var (totalAmount, transactionCount) = await transactionRepository.GetSellerVolumeAsync(tenantId, sellerId, since);

        logger.LogDebug(
            "Verificando elegibilidade para seller {SellerId}: volume R${Volume}, {Count} transacoes nos ultimos {Days} dias",
            sellerId, totalAmount, transactionCount, LookbackDays);

        // Check SCALA eligibility first (higher plan takes priority)
        if (totalAmount >= ScalaVolumeThreshold)
        {
            var scalaPlan = await pricingPlanRepository.GetByCodeAsync("SCALA");
            if (scalaPlan is not null && currentRank < GetPlanRank("SCALA"))
            {
                return new PlanEligibilityResult(
                    currentPlanCode,
                    EligiblePlanCode: "SCALA",
                    Reason: $"Volume de R${totalAmount:N2} nos ultimos 30 dias qualifica para o plano SCALA (minimo R${ScalaVolumeThreshold:N2}).",
                    CanUpgrade: true);
            }
        }

        // Check CRESCA eligibility
        if (currentRank < GetPlanRank("CRESCA"))
        {
            if (totalAmount >= CrescaVolumeThreshold)
            {
                var crescaPlan = await pricingPlanRepository.GetByCodeAsync("CRESCA");
                if (crescaPlan is not null)
                {
                    return new PlanEligibilityResult(
                        currentPlanCode,
                        EligiblePlanCode: "CRESCA",
                        Reason: $"Volume de R${totalAmount:N2} nos ultimos 30 dias qualifica para o plano CRESCA (minimo R${CrescaVolumeThreshold:N2}).",
                        CanUpgrade: true);
                }
            }

            if (transactionCount >= CrescaTransactionCountThreshold)
            {
                var crescaPlan = await pricingPlanRepository.GetByCodeAsync("CRESCA");
                if (crescaPlan is not null)
                {
                    return new PlanEligibilityResult(
                        currentPlanCode,
                        EligiblePlanCode: "CRESCA",
                        Reason: $"{transactionCount} transacoes capturadas nos ultimos 30 dias qualificam para o plano CRESCA (minimo {CrescaTransactionCountThreshold}).",
                        CanUpgrade: true);
                }
            }
        }

        return new PlanEligibilityResult(
            currentPlanCode,
            EligiblePlanCode: null,
            Reason: $"Volume de R${totalAmount:N2} e {transactionCount} transacoes nos ultimos 30 dias nao atingem os criterios para upgrade.",
            CanUpgrade: false);
    }

    private static int GetPlanRank(string planCode)
    {
        var index = Array.IndexOf(PlanHierarchy, planCode);
        return index >= 0 ? index : 0;
    }
}
