using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.Tenants.DTOs;

namespace FellowCore.Application.Modules.Tenants.Interfaces;

public interface ITenantService
{
    Task<TenantCreateResponse> CreateAsync(CreateTenantDto createTenantDto);
    Task<TenantResponse> GetByIdAsync(Guid tenantId);
    Task UpdateProvidersAsync(Guid tenantId, UpdateTenantProvidersDto dto);
    Task<RotateApiKeyResponse> RotateApiKeyAsync(Guid tenantId, string currentApiSecret);
}