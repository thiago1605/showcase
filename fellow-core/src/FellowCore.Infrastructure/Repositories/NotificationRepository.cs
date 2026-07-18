using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class NotificationRepository(AppDbContext context) : INotificationRepository
{
    public async Task<(IReadOnlyList<Notification> Items, int TotalCount)> GetPagedAsync(
        Guid tenantId,
        Guid sellerId,
        int skip,
        int take,
        bool unreadOnly = false)
    {
        var query = context.Notifications
            .Where(n => n.TenantId == tenantId && n.SellerId == sellerId);
        if (unreadOnly)
            query = query.Where(n => n.ReadAt == null);

        var totalCount = await query.CountAsync();
        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
        return (items, totalCount);
    }

    public async Task<int> GetUnreadCountAsync(Guid tenantId, Guid sellerId)
        => await context.Notifications
            .Where(n => n.TenantId == tenantId && n.SellerId == sellerId && n.ReadAt == null)
            .CountAsync();

    public async Task<Notification?> GetByIdAsync(Guid tenantId, Guid sellerId, Guid notificationId)
        => await context.Notifications
            .FirstOrDefaultAsync(n =>
                n.Id == notificationId &&
                n.TenantId == tenantId &&
                n.SellerId == sellerId);

    public async Task<int> MarkAllReadAsync(Guid tenantId, Guid sellerId)
    {
        // ExecuteUpdateAsync evita carregar entities em memória — útil quando o
        // seller tem centenas de notificações acumuladas. Bulk update direto no DB.
        var now = DateTime.UtcNow;
        return await context.Notifications
            .Where(n =>
                n.TenantId == tenantId &&
                n.SellerId == sellerId &&
                n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now));
    }

    public void Add(Notification notification) => context.Notifications.Add(notification);
    public void Update(Notification notification) => context.Notifications.Update(notification);
    public Task SaveChangesAsync() => context.SaveChangesAsync();
}
