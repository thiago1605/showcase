using System.Security.Cryptography;
using System.Text;
using FellowCore.Api.Extensions;
using FellowCore.Domain.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace FellowCore.Api.Auth;

/// <summary>
/// Allows the endpoint to authenticate either via JWT (validated by the standard
/// authentication middleware and translated to AuthInfo by JwtAuthContextMiddleware)
/// or via X-Api-Key header (validated here as a fallback). Use on endpoints that need
/// to be reachable both from the seller portal (JWT) and from B2B integrations (API key).
///
/// If neither mechanism produced an AuthInfo, the request is rejected with 401.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AuthOrApiKeyAuthAttribute : Attribute, IAsyncActionFilter
{
    private const string ApiKeyHeaderName = "X-Api-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // JwtAuthContextMiddleware already populated AuthInfo if a valid JWT was present.
        if (context.HttpContext.GetAuthInfo() != null)
        {
            await next();
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedApiKey)
            || string.IsNullOrWhiteSpace(extractedApiKey))
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                Message = "Autenticação obrigatória. Forneça um JWT válido (Authorization: Bearer) ou X-Api-Key."
            });
            return;
        }

        var apiKey = extractedApiKey.ToString();
        var apiKeyHash = ComputeApiKeyHash(apiKey);
        var cache = context.HttpContext.RequestServices.GetService<IDistributedCache>();
        var cacheKey = $"tenant:apikey:{apiKeyHash}";

        string? cachedTenantId = null;
        try
        {
            cachedTenantId = cache != null ? await cache.GetStringAsync(cacheKey) : null;
        }
        catch (Exception ex)
        {
            var logger = context.HttpContext.RequestServices.GetService<ILogger<AuthOrApiKeyAuthAttribute>>();
            logger?.LogWarning(ex, "Redis indisponível para cache de API key. Fallback para DB.");
        }

        Guid tenantId;
        if (cachedTenantId != null && Guid.TryParse(cachedTenantId, out tenantId))
        {
            context.HttpContext.SetAuthInfo(new AuthInfo { TenantId = tenantId, AuthType = AuthType.ApiKey });
            await next();
            return;
        }

        var tenantRepository = context.HttpContext.RequestServices.GetRequiredService<ITenantRepository>();
        var tenant = await tenantRepository.GetByApiKeyHashAsync(apiKeyHash);

        if (tenant == null)
        {
            context.Result = new UnauthorizedObjectResult(new { Message = "API Key inválida ou revogada." });
            return;
        }

        tenantId = tenant.Id;
        context.HttpContext.SetAuthInfo(new AuthInfo { TenantId = tenantId, AuthType = AuthType.ApiKey });

        try
        {
            if (cache != null)
            {
                await cache.SetStringAsync(cacheKey, tenantId.ToString(), new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
                });
            }
        }
        catch (Exception ex)
        {
            var logger = context.HttpContext.RequestServices.GetService<ILogger<AuthOrApiKeyAuthAttribute>>();
            logger?.LogWarning(ex, "Redis indisponível ao cachear API key. Próximo request fará DB lookup.");
        }

        await next();
    }

    private static string ComputeApiKeyHash(string apiKey)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexStringLower(hashBytes);
    }
}
