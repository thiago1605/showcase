using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FellowCore.Infrastructure.Repositories;

public class InboundWebhookEventRepository(
    AppDbContext context,
    ILogger<InboundWebhookEventRepository> logger) : IInboundWebhookEventRepository
{
    public async Task<InboundWebhookEvent?> TryRegisterReceivedAsync(
        PaymentProvider provider,
        string eventId,
        string eventType,
        CancellationToken ct = default)
    {
        // Verifica primeiro via SELECT (rota rápida quando é duplicado óbvio).
        // A garantia REAL é o unique constraint — a query abaixo só evita um
        // round-trip de INSERT em duplicações comuns.
        var existing = await context.InboundWebhookEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Provider == provider && e.EventId == eventId, ct);
        if (existing is not null)
        {
            logger.LogDebug("[INBOUND_WEBHOOK] Já registrado {Provider}/{EventId} — pulando.",
                provider, eventId);
            return null;
        }

        var entity = InboundWebhookEvent.CreateReceived(provider, eventId, eventType);
        context.InboundWebhookEvents.Add(entity);

        try
        {
            await context.SaveChangesAsync(ct);
            return entity;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Outro processo inseriu o mesmo (Provider, EventId) entre nosso SELECT
            // e SaveChanges. Comportamento esperado em concorrência alta. Detacha
            // pra não poluir o tracker.
            context.Entry(entity).State = EntityState.Detached;
            logger.LogDebug("[INBOUND_WEBHOOK] Race detectada em {Provider}/{EventId}; outro worker já tratou.",
                provider, eventId);
            return null;
        }
    }

    public async Task MarkProcessedAsync(Guid eventId, CancellationToken ct = default)
    {
        await context.InboundWebhookEvents
            .Where(e => e.Id == eventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Processed, true)
                .SetProperty(e => e.ProcessedAt, DateTime.UtcNow)
                .SetProperty(e => e.LastError, (string?)null), ct);
    }

    public async Task MarkFailedAsync(Guid eventId, string errorMessage, CancellationToken ct = default)
    {
        var trimmed = errorMessage.Length > 2000 ? errorMessage[..2000] : errorMessage;
        await context.InboundWebhookEvents
            .Where(e => e.Id == eventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Processed, false)
                .SetProperty(e => e.ProcessedAt, DateTime.UtcNow)
                .SetProperty(e => e.LastError, trimmed), ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505";
}
