using System.Security.Cryptography;
using System.Text;
using FellowCore.Application.Common.Interfaces;

namespace FellowCore.Api.Middlewares.Idempotency;

public class IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const string ApiKeyHeader = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context, IIdempotencyService idempotencyService)
    {
        var isPostToApi = context.Request.Method == "POST"
            && context.Request.Path.StartsWithSegments("/api/v1")
            && !context.Request.Path.StartsWithSegments("/api/v1/webhooks")
            && !context.Request.Path.StartsWithSegments("/api/v1/auth")
            && !context.Request.Path.StartsWithSegments("/api/v1/reconciliation");

        if (!isPostToApi)
        {
            await next(context);
            return;
        }

        // Se não veio o header, rejeita com 400
        if (!context.Request.Headers.TryGetValue(IdempotencyHeader, out var keyValues))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\": \"Header Idempotency-Key é obrigatório.\"}");
            return;
        }

        // Scope idempotency key per tenant (via API key hash) to prevent cross-tenant collisions
        var apiKey = context.Request.Headers[ApiKeyHeader].ToString();
        string tenantPrefix;
        if (!string.IsNullOrEmpty(apiKey))
        {
            tenantPrefix = ComputeKeyPrefix(apiKey);
        }
        else if (context.Request.Path.StartsWithSegments("/api/v1/payment-links/pay", out var remaining)
            && remaining.HasValue)
        {
            // For anonymous payment-link pay requests, scope by the link token (last path segment)
            var token = remaining.Value!.Trim('/').Split('/').Last();
            tenantPrefix = string.IsNullOrEmpty(token) ? "unknown" : $"pl_{token}";
        }
        else
        {
            tenantPrefix = "unknown";
        }
        var idempotencyKey = $"{tenantPrefix}:{keyValues}";

        IdempotencyResult? result;
        try
        {
            result = await idempotencyService.TryAcquireLockAsync(idempotencyKey);
        }
        catch (Exception ex)
        {
            // Redis unavailable — degrade gracefully: allow request without dedup
            logger.LogWarning(ex, "Redis indisponivel para idempotencia. Prosseguindo sem dedup para key {Key}", idempotencyKey);
            await next(context);
            return;
        }

        if (result.AlreadyProcessed)
        {
            // Já concluído — devolve a resposta original cacheada
            if (result.CachedResponse != null)
            {
                context.Response.StatusCode = result.CachedStatusCode ?? StatusCodes.Status200OK;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(result.CachedResponse);
                return;
            }

            // Em andamento — outro request paralelo está processando
            context.Response.StatusCode = StatusCodes.Status423Locked;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\": \"Requisição em andamento. Tente novamente em instantes.\"}");
            return;
        }

        // Substitui o body stream para conseguir ler a resposta depois
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);

            // Captura o body da resposta
            buffer.Seek(0, SeekOrigin.Begin);
            var responseBody = await new StreamReader(buffer).ReadToEndAsync();

            // Salva no cache só se foi sucesso
            try
            {
                if (context.Response.StatusCode is >= 200 and < 300)
                    await idempotencyService.CompleteAsync(idempotencyKey, responseBody, context.Response.StatusCode);
                else
                    await idempotencyService.ReleaseLockAsync(idempotencyKey);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Redis indisponivel ao finalizar idempotencia para key {Key}", idempotencyKey);
            }

            // Escreve de volta na resposta original
            buffer.Seek(0, SeekOrigin.Begin);
            await buffer.CopyToAsync(originalBody);
        }
        catch
        {
            try { await idempotencyService.ReleaseLockAsync(idempotencyKey); }
            catch (Exception ex) { logger.LogWarning(ex, "Redis indisponivel ao liberar lock para key {Key}", idempotencyKey); }
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static string ComputeKeyPrefix(string apiKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexStringLower(hash)[..16];
    }
}