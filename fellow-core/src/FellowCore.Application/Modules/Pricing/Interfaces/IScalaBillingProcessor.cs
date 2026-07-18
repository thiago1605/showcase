namespace FellowCore.Application.Modules.Pricing.Interfaces;

public interface IScalaBillingProcessor
{
    Task ProcessMonthlyBillingAsync(CancellationToken ct = default);
}
