using FellowCore.Domain.Entities;

namespace FellowCore.Application.Modules.AuditLogs.DTOs;

public record AuditLogResponseDto(
    Guid Id,
    Guid TenantId,
    string Action,
    string? ResourceId,
    string? IpAddress,
    string? CorrelationId,
    int StatusCode,
    DateTime CreatedAt
)
{
    public static AuditLogResponseDto FromEntity(AuditLog log) => new(
        log.Id,
        log.TenantId,
        log.Action,
        log.ResourceId,
        log.IpAddress,
        log.CorrelationId,
        log.StatusCode,
        log.CreatedAt
    );
}
