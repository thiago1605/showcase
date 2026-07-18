using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class PricingPlanRepository(AppDbContext _context) : IPricingPlanRepository
{
    public async Task<PricingPlan?> GetByIdAsync(Guid id)
    {
        return await _context.PricingPlans.FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<PricingPlan?> GetByCodeAsync(string code)
    {
        return await _context.PricingPlans.FirstOrDefaultAsync(p => p.Code == code && p.IsActive);
    }

    public async Task<IReadOnlyList<PricingPlan>> GetAllActiveAsync()
    {
        return await _context.PricingPlans.Where(p => p.IsActive).OrderBy(p => p.MonthlyFee).ToListAsync();
    }

    public void Add(PricingPlan plan) => _context.PricingPlans.Add(plan);
    public void Update(PricingPlan plan) => _context.PricingPlans.Update(plan);

    public async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
    }
}
