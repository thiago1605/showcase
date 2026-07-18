using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IWebhookDeliveryRepository
{
    /// <summary>
    /// Loads a delivery by its ID. Tenant ownership is verified by checking d.Endpoint.TenantId.
    /// </summary>
    Task<WebhookDelivery?> GetByIdAsync(Guid tenantId, Guid deliveryId);
    Task<List<WebhookDelivery>> GetPendingRetriesAsync(DateTime before, int batchSize = 50);
    Task<(List<WebhookDelivery> Items, int TotalCount)> GetByEndpointPagedAsync(Guid endpointId, int skip, int take);
    /// <summary>
    /// Returns dead-letter deliveries scoped to a specific tenant (via d.Endpoint.TenantId).
    /// </summary>
    Task<List<WebhookDelivery>> GetDeadLettersAsync(Guid tenantId, int limit = 100);
    /// <summary>
    /// Counts dead-letter deliveries scoped to a specific tenant (via d.Endpoint.TenantId).
    /// </summary>
    Task<int> GetDeadLetterCountAsync(Guid tenantId);
    /// <summary>
    /// Counts failed deliveries for a tenant since a given timestamp.
    /// </summary>
    Task<int> GetFailedCountSinceAsync(Guid tenantId, DateTime since);
    void Update(WebhookDelivery delivery);
    Task SaveChangesAsync();
}
