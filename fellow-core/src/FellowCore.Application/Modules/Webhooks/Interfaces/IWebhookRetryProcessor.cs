namespace FellowCore.Application.Modules.Webhooks.Interfaces;

public interface IWebhookRetryProcessor
{
    Task ProcessPendingRetriesAsync(CancellationToken ct = default);
}
