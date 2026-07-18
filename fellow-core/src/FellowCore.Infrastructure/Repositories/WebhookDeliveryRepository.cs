using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class WebhookDeliveryRepository(AppDbContext context) : IWebhookDeliveryRepository
{
    public async Task<WebhookDelivery?> GetByIdAsync(Guid tenantId, Guid deliveryId)
        => await context.Set<WebhookDelivery>()
            .Include(d => d.Endpoint)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.Endpoint.TenantId == tenantId);

    public async Task<List<WebhookDelivery>> GetPendingRetriesAsync(DateTime before, int batchSize = 50)
        => await context.Set<WebhookDelivery>()
            .Include(d => d.Endpoint)
            .Where(d => d.Status == DeliveryStatus.PENDING_RETRY && d.NextRetryAt <= before)
            .OrderBy(d => d.NextRetryAt)
            .Take(batchSize)
            .ToListAsync();

    public async Task<(List<WebhookDelivery> Items, int TotalCount)> GetByEndpointPagedAsync(Guid endpointId, int skip, int take)
    {
        var query = context.Set<WebhookDelivery>().Where(d => d.EndpointId == endpointId);
        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
        return (items, totalCount);
    }

    public async Task<List<WebhookDelivery>> GetDeadLettersAsync(Guid tenantId, int limit = 100)
        => await context.Set<WebhookDelivery>()
            .Include(d => d.Endpoint)
            .Where(d => d.Status == DeliveryStatus.FAILED && d.Endpoint.TenantId == tenantId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(limit)
            .ToListAsync();

    public async Task<int> GetDeadLetterCountAsync(Guid tenantId)
        => await context.Set<WebhookDelivery>()
            .CountAsync(d => d.Status == DeliveryStatus.FAILED && d.Endpoint.TenantId == tenantId);

    public async Task<int> GetFailedCountSinceAsync(Guid tenantId, DateTime since)
        => await context.Set<WebhookDelivery>()
            .CountAsync(d => d.Status == DeliveryStatus.FAILED
                          && d.Endpoint.TenantId == tenantId
                          && d.CreatedAt >= since);

    public void Update(WebhookDelivery delivery) => context.Set<WebhookDelivery>().Update(delivery);
    public Task SaveChangesAsync() => context.SaveChangesAsync();
}
