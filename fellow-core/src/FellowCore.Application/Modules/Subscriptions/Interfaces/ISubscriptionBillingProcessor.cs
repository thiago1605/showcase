namespace FellowCore.Application.Modules.Subscriptions.Interfaces;

public interface ISubscriptionBillingProcessor
{
    Task ProcessDueBillingAsync(CancellationToken cancellationToken = default);
}
