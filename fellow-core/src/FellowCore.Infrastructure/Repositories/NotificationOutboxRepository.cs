using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class NotificationOutboxRepository(AppDbContext context) : INotificationOutboxRepository
{
    public void Add(NotificationOutbox message) => context.NotificationOutbox.Add(message);

    public async Task<IReadOnlyList<NotificationOutbox>> GetDueAsync(int batchSize, DateTime now)
        => await context.NotificationOutbox
            .Where(o => o.ProcessedAt == null && o.NextAttemptAt <= now)
            .OrderBy(o => o.CreatedAt)
            .Take(batchSize)
            .ToListAsync();

    public void Update(NotificationOutbox message) => context.NotificationOutbox.Update(message);
    public Task SaveChangesAsync() => context.SaveChangesAsync();
}
