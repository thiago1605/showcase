using System.Text.Json;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Domain.Primitives;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Database;

public class UnitOfWork(AppDbContext context, IDomainEventDispatcher dispatcher, ILogger<UnitOfWork> logger) : IUnitOfWork
{
    private IDbContextTransaction? _dbTransaction;

    public async Task BeginAsync()
    {
        _dbTransaction = await context.Database.BeginTransactionAsync();
    }

    public async Task CommitAsync()
    {
        // Collect domain events from tracked aggregates BEFORE saving
        var aggregates = context.ChangeTracker
            .Entries<IAggregateRoot>()
            .Select(e => e.Entity)
            .Where(a => a.DomainEvents.Count > 0)
            .ToList();

        var events = aggregates
            .SelectMany(a => a.DomainEvents)
            .ToList();

        aggregates.ForEach(a => a.ClearDomainEvents());

        // Persist events as outbox messages within the same transaction
        foreach (var domainEvent in events)
        {
            var outboxMessage = OutboxMessage.Create(
                eventType: domainEvent.GetType().AssemblyQualifiedName!,
                payload: JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
                occurredAt: domainEvent.OccurredAt);

            context.OutboxMessages.Add(outboxMessage);
        }

        // Flush all tracked changes (entities + outbox messages) to DB
        await context.SaveChangesAsync();

        if (_dbTransaction != null)
        {
            await _dbTransaction.CommitAsync();
            await _dbTransaction.DisposeAsync();
            _dbTransaction = null;
        }

        // Best-effort immediate dispatch for low latency
        await DispatchAndMarkProcessedAsync(events);
    }

    public async Task RollbackAsync()
    {
        if (_dbTransaction != null)
        {
            await _dbTransaction.RollbackAsync();
            await _dbTransaction.DisposeAsync();
            _dbTransaction = null;
        }
    }

    public void Dispose()
    {
        _dbTransaction?.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task DispatchAndMarkProcessedAsync(List<IDomainEvent> events)
    {
        if (events.Count == 0) return;

        try
        {
            await dispatcher.DispatchAsync(events);

            // Mark outbox messages as processed
            var recentMessages = await context.OutboxMessages
                .Where(m => m.ProcessedAt == null)
                .OrderByDescending(m => m.OccurredAt)
                .Take(events.Count)
                .ToListAsync();

            foreach (var msg in recentMessages)
                msg.MarkProcessed();

            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Non-fatal: outbox processor will retry
            logger.LogWarning(ex, "Falha no dispatch imediato de {Count} domain events. Outbox processor fará retry.", events.Count);
        }
    }
}
