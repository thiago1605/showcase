using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.AuditLogs.DTOs;
using FellowCore.Application.Modules.AuditLogs.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.AuditLogs.Services;

public class AuditLogService(IAuditLogRepository repository) : IAuditLogService
{
    public async Task LogAsync(Guid tenantId, string action, string? resourceId, string? ipAddress, string? correlationId, int statusCode)
    {
        var log = AuditLog.Create(tenantId, action, resourceId, ipAddress, correlationId, statusCode);
        await repository.AddAsync(log);
    }

    public async Task<PagedResult<AuditLogResponseDto>> ListAsync(Guid tenantId, string? action, int page, int pageSize)
    {
        var (skip, take, normalizedPage) = PagedResult<AuditLogResponseDto>.Normalize(page, pageSize);
        var (items, total) = await repository.ListByTenantAsync(tenantId, action, skip, take);
        var dtos = items.Select(AuditLogResponseDto.FromEntity).ToList();
        return new PagedResult<AuditLogResponseDto>(dtos, total, normalizedPage, take);
    }
}
