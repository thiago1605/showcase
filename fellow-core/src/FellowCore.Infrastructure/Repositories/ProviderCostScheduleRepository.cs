using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class ProviderCostScheduleRepository(AppDbContext context) : IProviderCostScheduleRepository
{
    public async Task<ProviderCostSchedule?> GetAsync(PaymentProvider provider, PaymentType paymentType)
        => await context.ProviderCostSchedules
            .FirstOrDefaultAsync(s => s.Provider == provider && s.PaymentType == paymentType && s.IsActive);

    public async Task<IReadOnlyList<ProviderCostSchedule>> GetAllAsync()
        => await context.ProviderCostSchedules.ToListAsync();

    public void Add(ProviderCostSchedule schedule)
        => context.ProviderCostSchedules.Add(schedule);

    public async Task SaveChangesAsync()
        => await context.SaveChangesAsync();
}
