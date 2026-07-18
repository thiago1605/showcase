using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

/// <summary>
/// Outbox de notificações — interface mínima pro producer (Add) e o
/// worker assíncrono (GetDueAsync + Update). Sem SaveChangesAsync no producer
/// porque o IUnitOfWork do caller commita junto da operação principal
/// (atomicidade). O processor tem seu próprio SaveChanges depois de marcar
/// processado/falha.
/// </summary>
public interface INotificationOutboxRepository
{
    /// <summary>
    /// Enfileira mensagem. NÃO chama SaveChanges — deixa o caller commitar
    /// junto da transaction principal (atomicidade outbox pattern).
    /// </summary>
    void Add(NotificationOutbox message);

    /// <summary>
    /// Pendentes (ProcessedAt IS NULL) com NextAttemptAt &lt;= now. Limitado por
    /// batchSize pra evitar carregar tudo em memória num burst. OrderBy
    /// CreatedAt pra FIFO de delivery (mensagens mais antigas primeiro).
    /// </summary>
    Task<IReadOnlyList<NotificationOutbox>> GetDueAsync(int batchSize, DateTime now);

    void Update(NotificationOutbox message);
    Task SaveChangesAsync();
}
