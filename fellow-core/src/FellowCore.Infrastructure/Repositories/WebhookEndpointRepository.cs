using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class WebhookEndpointRepository(AppDbContext context) : IWebhookEndpointRepository
{
    public async Task<List<WebhookEndpoint>> GetActiveByTenantAsync(Guid tenantId)
        => await context.WebhookEndpoints
            .Where(e => e.TenantId == tenantId && e.Enabled)
            .ToListAsync();

    public async Task<List<WebhookEndpoint>> GetActiveTenantWideAsync(Guid tenantId)
        => await context.WebhookEndpoints
            .Where(e => e.TenantId == tenantId && e.Enabled && e.SellerId == null)
            .ToListAsync();

    public async Task<List<WebhookEndpoint>> GetActiveForSellerEventAsync(Guid tenantId, Guid sellerId)
        => await context.WebhookEndpoints
            .Where(e => e.TenantId == tenantId
                && e.Enabled
                && (e.SellerId == null || e.SellerId == sellerId))
            .ToListAsync();

    public async Task<List<WebhookEndpoint>> GetBySellerAsync(Guid tenantId, Guid sellerId)
        => await context.WebhookEndpoints
            .Where(e => e.TenantId == tenantId && e.Enabled && e.SellerId == sellerId)
            .ToListAsync();

    public async Task<(IReadOnlyList<WebhookEndpoint> Items, int TotalCount)> GetPagedAsync(Guid tenantId, int skip, int take)
    {
        var query = context.WebhookEndpoints.Where(e => e.TenantId == tenantId && e.Enabled);
        var totalCount = await query.CountAsync();
        var items = await query.OrderByDescending(e => e.CreatedAt).Skip(skip).Take(take).ToListAsync();
        return (items, totalCount);
    }

    public async Task<(IReadOnlyList<WebhookEndpoint> Items, int TotalCount)> GetPagedByScopeAsync(Guid tenantId, Guid? sellerId, int skip, int take)
    {
        var query = context.WebhookEndpoints
            .Where(e => e.TenantId == tenantId && e.Enabled && e.SellerId == sellerId);
        var totalCount = await query.CountAsync();
        var items = await query.OrderByDescending(e => e.CreatedAt).Skip(skip).Take(take).ToListAsync();
        return (items, totalCount);
    }

    public async Task<WebhookEndpoint?> GetByIdAsync(Guid tenantId, Guid endpointId)
        => await context.WebhookEndpoints
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == endpointId);

    public async Task<WebhookEndpoint?> GetEnabledByUrlAsync(Guid tenantId, string url, Guid? sellerId = null)
        => await context.WebhookEndpoints
            .FirstOrDefaultAsync(e => e.TenantId == tenantId
                && e.Enabled
                && e.Url == url
                && e.SellerId == sellerId);

    public void Add(WebhookEndpoint endpoint) => context.WebhookEndpoints.Add(endpoint);
    public void Update(WebhookEndpoint endpoint) => context.WebhookEndpoints.Update(endpoint);
    public Task SaveChangesAsync() => context.SaveChangesAsync();
}
