using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.AuditLogs.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Tenant-wide audit log access. Operação interna — JWT-only, restrito a platform
/// operators (SUPER_ADMIN/OWNER/FINANCE sem seller_id no token). Sellers (mesmo OWNER
/// vinculados a um SellerId) recebem 403 — eles não devem poder ver ações de outros
/// sellers do tenant.
/// </summary>
[ApiController]
[Route("api/v1/audit-logs")]
[Authorize(Roles = "SUPER_ADMIN,OWNER,FINANCE")]
[EnableRateLimiting("fixed")]
public class AuditLogsController(IAuditLogService auditLogService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? action = null)
    {
        if (this.EnforceOperatorOr403() is { } block) return block;

        Guid tenantId = HttpContext.GetTenantId();
        var result = await auditLogService.ListAsync(tenantId, action, page, pageSize);
        return Ok(result);
    }
}
