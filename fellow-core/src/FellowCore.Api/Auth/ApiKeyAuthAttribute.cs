using System.Security.Cryptography;
using System.Text;
using FellowCore.Api.Extensions;
using FellowCore.Domain.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace FellowCore.Api.Auth;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiKeyAuthAttribute : Attribute, IAsyncActionFilter
{
    private const string ApiKeyHeaderName = "X-Api-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Respeita [AllowAnonymous] no método — quando presente, pula a validação.
        // Padrão necessário pra endpoints públicos (ex: /pricing-plans/simulate)
        // em controllers marcados [ApiKeyAuth] no nível da classe.
        if (context.ActionDescriptor.EndpointMetadata
            .OfType<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>().Any())
        {
            await next();
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedApiKey)
            || string.IsNullOrWhiteSpace(extractedApiKey))
        {
            context.Result = new UnauthorizedObjectResult(new { Message = "API Key nao fornecida no header X-Api-Key." });
            return;
        }

        string apiKey = extractedApiKey.ToString();
        string apiKeyHash = ComputeApiKeyHash(apiKey);

        var cache = context.HttpContext.RequestServices.GetService<IDistributedCache>();
        string cacheKey = $"tenant:apikey:{apiKeyHash}";
        Guid tenantId;

        if (cache == null)
        {
            var logger = context.HttpContext.RequestServices.GetService<ILogger<ApiKeyAuthAttribute>>();
            logger?.LogWarning("IDistributedCache indisponivel. API key sera validada sem cache a cada request.");
        }

        string? cachedTenantId = null;
        try
        {
            cachedTenantId = cache != null ? await cache.GetStringAsync(cacheKey) : null;
        }
        catch (Exception ex)
        {
            var logger = context.HttpContext.RequestServices.GetService<ILogger<ApiKeyAuthAttribute>>();
            logger?.LogWarning(ex, "Redis indisponivel para cache de API key. Fallback para DB.");
        }

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
            context.Result = new UnauthorizedObjectResult(new { Message = "API Key invalida ou revogada." });
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
            var logger = context.HttpContext.RequestServices.GetService<ILogger<ApiKeyAuthAttribute>>();
            logger?.LogWarning(ex, "Redis indisponivel ao cachear API key. Proximo request fara DB lookup.");
        }

        await next();
    }

    private static string ComputeApiKeyHash(string apiKey)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexStringLower(hashBytes);
    }
}
