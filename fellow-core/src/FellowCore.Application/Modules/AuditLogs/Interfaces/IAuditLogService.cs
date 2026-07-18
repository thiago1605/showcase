using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.AuditLogs.DTOs;

namespace FellowCore.Application.Modules.AuditLogs.Interfaces;

public interface IAuditLogService
{
    Task LogAsync(Guid tenantId, string action, string? resourceId, string? ipAddress, string? correlationId, int statusCode);
    Task<PagedResult<AuditLogResponseDto>> ListAsync(Guid tenantId, string? action, int page, int pageSize);
}
