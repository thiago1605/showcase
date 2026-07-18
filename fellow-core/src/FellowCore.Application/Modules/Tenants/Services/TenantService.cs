using System.Security.Cryptography;
using System.Text;
using FellowCore.Application.Common.Models;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Tenants.DTOs;
using FellowCore.Application.Modules.Tenants.Interfaces;
using FellowCore.Application.Common.Utils;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Caching.Distributed;

namespace FellowCore.Application.Modules.Tenants.Services;

public class TenantService(ITenantRepository tenantRepository, IDistributedCache? cache = null, bool isProduction = true) : ITenantService
{
    private string KeyPrefix => isProduction ? "live" : "test";

    public async Task<TenantCreateResponse> CreateAsync(CreateTenantDto dto)
    {
        var existingTenant = await tenantRepository.GetBySlugAsync(dto.Slug);
        if (existingTenant != null)
            throw new ConflictException("Tenant.DuplicateSlug", "Já existe um tenant com esse slug.");

        var apiKey = $"pk_{KeyPrefix}_{CryptoUtils.GenerateRandomHex(16)}";
        var apiKeyHash = CryptoUtils.GenerateSha256Hash(apiKey);
        var apiKeyPrefix = apiKey[..12];
        var apiSecret = $"sk_{KeyPrefix}_{CryptoUtils.GenerateRandomHex(16)}";
        var apiSecretHash = CryptoUtils.GenerateSha256Hash(apiSecret);

        // Never persist the plaintext apiSecret in the entity — keep it as a local variable
        // only so it can be returned to the user in the response.
        var tenant = Tenant.Create(dto.Name, dto.Slug, apiKeyHash, apiKeyPrefix, apiSecretHash, ownerEmail: dto.OwnerEmail);

        Tenant savedTenant = await tenantRepository.AddAsync(tenant);

        TenantResponse tenantResponse = new(savedTenant.Id, savedTenant.Name, savedTenant.Slug, MaskApiKey(savedTenant.ApiKeyPrefix), savedTenant.CreatedAt);

        return new TenantCreateResponse(tenantResponse, apiKey, apiSecret);
    }

    public async Task<TenantResponse> GetByIdAsync(Guid tenantId)
    {
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
            ?? throw new NotFoundException("Tenant.NotFound", "Tenant nao encontrado.");

        return new TenantResponse(tenant.Id, tenant.Name, tenant.Slug, MaskApiKey(tenant.ApiKeyPrefix), tenant.CreatedAt);
    }

    public async Task<RotateApiKeyResponse> RotateApiKeyAsync(Guid tenantId, string currentApiSecret)
    {
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
            ?? throw new NotFoundException("Tenant.NotFound", "Tenant nao encontrado.");

        var providedHash = CryptoUtils.GenerateSha256Hash(currentApiSecret);
        var storedHash = tenant.ApiSecretHash;

        var providedBytes = Encoding.UTF8.GetBytes(providedHash);
        var storedBytes = Encoding.UTF8.GetBytes(storedHash);
        if (!CryptographicOperations.FixedTimeEquals(providedBytes, storedBytes))
            throw new UnauthorizedException("Tenant.InvalidSecret", "ApiSecret atual invalido.");

        var oldApiKeyHash = tenant.ApiKeyHash;

        var newApiKey = $"pk_{KeyPrefix}_{CryptoUtils.GenerateRandomHex(16)}";
        var newApiKeyHash = CryptoUtils.GenerateSha256Hash(newApiKey);
        var newApiKeyPrefix = newApiKey[..12];
        var newApiSecret = $"sk_{KeyPrefix}_{CryptoUtils.GenerateRandomHex(16)}";
        var newApiSecretHash = CryptoUtils.GenerateSha256Hash(newApiSecret);

        tenant.RotateApiKey(newApiKeyHash, newApiKeyPrefix, newApiSecretHash);
        await tenantRepository.SaveChangesAsync();

        if (cache != null)
            await cache.RemoveAsync($"tenant:apikey:{oldApiKeyHash}");

        return new RotateApiKeyResponse(newApiKey, newApiSecret);
    }

    public async Task UpdateProvidersAsync(Guid tenantId, UpdateTenantProvidersDto dto)
    {
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
            ?? throw new NotFoundException("Tenant.NotFound", "Tenant nao encontrado.");

        if (tenant.Config == null)
            throw new BusinessException("Tenant.NoConfig", "Tenant sem configuracao de pagamento ativa.");

        if (dto.ActivePixProvider.HasValue)
            tenant.Config.SetActivePixProvider(dto.ActivePixProvider.Value);

        if (dto.ActiveCreditProvider.HasValue)
            tenant.Config.SetActiveCreditProvider(dto.ActiveCreditProvider.Value);

        await tenantRepository.SaveChangesAsync();
    }

    private static string MaskApiKey(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return "****";
        return $"{prefix}****";
    }
}
