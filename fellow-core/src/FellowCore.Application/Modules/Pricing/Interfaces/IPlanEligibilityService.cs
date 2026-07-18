namespace FellowCore.Application.Modules.Pricing.Interfaces;

public record PlanEligibilityResult(
    string CurrentPlanCode,
    string? EligiblePlanCode,
    string Reason,
    bool CanUpgrade);

public interface IPlanEligibilityService
{
    Task<PlanEligibilityResult> CheckEligibilityAsync(Guid tenantId, Guid sellerId);
}
