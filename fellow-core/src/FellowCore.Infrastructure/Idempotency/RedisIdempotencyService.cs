// FellowCore.Infrastructure/Idempotency/RedisIdempotencyService.cs
using FellowCore.Application.Common.Interfaces;
using StackExchange.Redis;
using System.Text.Json;

namespace FellowCore.Infrastructure.Idempotency;

public class RedisIdempotencyService(IConnectionMultiplexer redis) : IIdempotencyService
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly TimeSpan _lockExpiration = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _completionExpiration = TimeSpan.FromHours(24);

    public async Task<IdempotencyResult> TryAcquireLockAsync(string idempotencyKey)
    {
        var cacheKey = GetCacheKey(idempotencyKey);

        // Verifica se já existe (processado ou em andamento)
        var existing = await _db.StringGetAsync(cacheKey);
        if (existing.HasValue)
        {
            var entry = JsonSerializer.Deserialize<IdempotencyEntry>((string)existing!);

            // Se já foi concluído, devolve a resposta cacheada
            if (entry?.Status == "Completed")
                return new IdempotencyResult(true, entry.ResponseBody, entry.StatusCode);

            // Se ainda está em andamento (outro request paralelo)
            if (entry?.Status == "Processing")
                return new IdempotencyResult(true, null);
        }

        // SetNX — atômico: só grava se a chave NÃO existir
        var lockEntry = JsonSerializer.Serialize(new IdempotencyEntry("Processing", null));
        var acquired = await _db.StringSetAsync(
            cacheKey,
            lockEntry,
            _lockExpiration,
            When.NotExists  
        );

        // Se não conseguiu o lock, outro request ganhou na corrida
        if (!acquired)
            return new IdempotencyResult(true, null);

        return new IdempotencyResult(false, null);
    }

    public async Task CompleteAsync(string idempotencyKey, string responseBody, int statusCode)
    {
        var cacheKey = GetCacheKey(idempotencyKey);
        var entry = JsonSerializer.Serialize(new IdempotencyEntry("Completed", responseBody, statusCode));

        await _db.StringSetAsync(cacheKey, entry, _completionExpiration);
    }

    public async Task ReleaseLockAsync(string idempotencyKey)
    {
        var cacheKey = GetCacheKey(idempotencyKey);
        await _db.KeyDeleteAsync(cacheKey);
    }

    private static string GetCacheKey(string key) => $"Idempotency:{key}";

    private record IdempotencyEntry(string Status, string? ResponseBody, int? StatusCode = null);
}