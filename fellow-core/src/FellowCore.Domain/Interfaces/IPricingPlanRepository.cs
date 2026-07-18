using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IPricingPlanRepository
{
    Task<PricingPlan?> GetByIdAsync(Guid id);
    Task<PricingPlan?> GetByCodeAsync(string code);
    Task<IReadOnlyList<PricingPlan>> GetAllActiveAsync();
    void Add(PricingPlan plan);
    void Update(PricingPlan plan);
    Task SaveChangesAsync();
}
