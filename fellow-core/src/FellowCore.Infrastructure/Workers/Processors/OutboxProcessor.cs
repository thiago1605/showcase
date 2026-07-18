using System.Text.Json;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Primitives;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public class OutboxProcessor(AppDbContext context, IDomainEventDispatcher dispatcher, ILogger<OutboxProcessor> logger)
{
    private const int MaxRetries = 5;
    private const int BatchSize = 50;

    public async Task ProcessAsync()
    {
        var messages = await context.OutboxMessages
            .Where(m => m.ProcessedAt == null && m.RetryCount < MaxRetries)
            .OrderBy(m => m.OccurredAt)
            .Take(BatchSize)
            .ToListAsync();

        if (messages.Count == 0) return;

        logger.LogInformation("Outbox processor: {Count} mensagens pendentes", messages.Count);

        foreach (var message in messages)
        {
            try
            {
                var eventType = Type.GetType(message.EventType);
                if (eventType == null)
                {
                    logger.LogError("Outbox: tipo de evento nao encontrado: {EventType}", message.EventType);
                    MoveToDeadLetterIfExhausted(message, $"Type not found: {message.EventType}");
                    continue;
                }

                var domainEvent = JsonSerializer.Deserialize(message.Payload, eventType) as IDomainEvent;
                if (domainEvent == null)
                {
                    MoveToDeadLetterIfExhausted(message, "Failed to deserialize domain event");
                    continue;
                }

                await dispatcher.DispatchAsync([domainEvent]);
                message.MarkProcessed();
            }
            catch (Exception ex)
            {
                var errorText = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                logger.LogError(ex, "Outbox: falha ao processar mensagem {MessageId}", message.Id);
                MoveToDeadLetterIfExhausted(message, errorText);
            }
        }

        await context.SaveChangesAsync();
    }

    private void MoveToDeadLetterIfExhausted(Domain.Entities.OutboxMessage message, string error)
    {
        // RetryCount is incremented inside MarkFailed, so check *before* incrementing
        if (message.RetryCount + 1 >= MaxRetries)
        {
            message.MarkDeadLetter(error);
            logger.LogCritical(
                "Outbox DLQ: message {MessageId} (type: {EventType}) exceeded {MaxRetries} retries and has been moved to dead-letter. Last error: {Error}",
                message.Id, message.EventType, MaxRetries, error);
        }
        else
        {
            message.MarkFailed(error);
        }
    }
}
