using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Interfaces;

/// <summary>
/// Repository pra idempotência de webhooks INBOUND (provider → FellowCore).
/// Dedup via unique index (Provider, EventId) na tabela InboundWebhookEvents.
/// </summary>
public interface IInboundWebhookEventRepository
{
    /// <summary>
    /// Tenta registrar o evento como "recebido". Retorna a entity criada,
    /// OU <c>null</c> quando já existe (duplicado). Caller usa null como
    /// sinal de "já processado, pula".
    /// </summary>
    Task<InboundWebhookEvent?> TryRegisterReceivedAsync(
        PaymentProvider provider,
        string eventId,
        string eventType,
        CancellationToken ct = default);

    /// <summary>Marca o evento como processado com sucesso.</summary>
    Task MarkProcessedAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>Marca o evento como falho — caller decide se retenta.</summary>
    Task MarkFailedAsync(Guid eventId, string errorMessage, CancellationToken ct = default);
}
