namespace FellowCore.Application.Common.Interfaces;

public record IdempotencyResult(bool AlreadyProcessed, string? CachedResponse, int? CachedStatusCode = null);

public interface IIdempotencyService
{
    /// <summary>
    /// Tenta adquirir o lock atomicamente. Retorna resposta cacheada se já foi processado.
    /// </summary>
    Task<IdempotencyResult> TryAcquireLockAsync(string idempotencyKey);

    /// <summary>
    /// Salva a resposta (com status code) e libera o lock após processamento bem-sucedido.
    /// </summary>
    Task CompleteAsync(string idempotencyKey, string responseBody, int statusCode);

    /// <summary>
    /// Remove o lock em caso de falha, permitindo retry.
    /// </summary>
    Task ReleaseLockAsync(string idempotencyKey);
}