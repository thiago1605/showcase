using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Interfaces;

public interface IProviderCostScheduleRepository
{
    Task<ProviderCostSchedule?> GetAsync(PaymentProvider provider, PaymentType paymentType);
    Task<IReadOnlyList<ProviderCostSchedule>> GetAllAsync();
    void Add(ProviderCostSchedule schedule);
    Task SaveChangesAsync();
}
