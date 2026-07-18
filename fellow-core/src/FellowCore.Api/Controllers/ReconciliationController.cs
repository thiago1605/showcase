using FellowCore.Api.Extensions;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Reconciliation admin operations — platform-operator only. The role check via
/// [Authorize(Roles=...)] is necessary but NOT sufficient: a seller-OWNER (JWT with
/// seller_id) would otherwise pass the role gate. EnforceOperatorOr403() closes that
/// hole by also requiring SellerId == null on the JWT.
/// </summary>
[ApiController]
[Route("api/v1/reconciliation")]
[Authorize(Roles = "SUPER_ADMIN,OWNER,FINANCE")]
[EnableRateLimiting("fixed")]
public class ReconciliationController(
    IReconciliationRepository reconciliationRepository) : ControllerBase
{
    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns([FromQuery] int limit = 10)
    {
        if (this.EnforceOperatorOr403() is { } block) return block;
        var tenantId = HttpContext.GetTenantId();
        var runs = await reconciliationRepository.GetRecentRunsAsync(tenantId, limit);
        return Ok(runs.Select(r => new
        {
            r.Id,
            r.TenantId,
            r.RunType,
            r.Status,
            r.StartedAt,
            r.CompletedAt,
            r.TransactionsChecked,
            r.IssuesFound,
            r.PlatformDriftCents,
            IssueCount = r.Issues.Count
        }));
    }

    [HttpGet("issues")]
    public async Task<IActionResult> GetIssues(
        [FromQuery] string? resolution = null,
        [FromQuery] string? severity = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0)
    {
        if (this.EnforceOperatorOr403() is { } block) return block;
        var tenantId = HttpContext.GetTenantId();
        var issues = await reconciliationRepository.GetIssuesAsync(tenantId, resolution, severity, limit, offset);
        return Ok(issues.Select(i => new
        {
            i.Id,
            i.RunId,
            i.TenantId,
            Type = i.Type.ToString(),
            i.Severity,
            i.InternalId,
            i.ExternalId,
            i.ExpectedCents,
            i.ActualCents,
            i.DriftCents,
            i.Description,
            i.Resolution,
            i.ResolutionNotes,
            i.ResolvedBy,
            i.ResolvedAt,
            i.CreatedAt
        }));
    }

    [HttpPost("issues/{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id)
    {
        if (this.EnforceOperatorOr403() is { } block) return block;
        var tenantId = HttpContext.GetTenantId();
        var issue = await reconciliationRepository.GetIssueByIdAsync(tenantId, id);
        if (issue is null)
            return NotFound();

        if (issue.Resolution != "OPEN")
            return BadRequest($"Issue is already {issue.Resolution}");

        var userId = HttpContext.GetCurrentUserId()?.ToString();
        issue.Acknowledge(userId);
        await reconciliationRepository.SaveChangesAsync();
        return Ok(new { issue.Id, issue.Resolution, issue.ResolvedBy });
    }

    [HttpPost("issues/{id:guid}/investigate")]
    public async Task<IActionResult> Investigate(Guid id)
    {
        if (this.EnforceOperatorOr403() is { } block) return block;
        var tenantId = HttpContext.GetTenantId();
        var issue = await reconciliationRepository.GetIssueByIdAsync(tenantId, id);
        if (issue is null)
            return NotFound();

        if (issue.Resolution is not ("OPEN" or "ACKNOWLEDGED"))
            return BadRequest($"Issue is already {issue.Resolution}");

        var userId = HttpContext.GetCurrentUserId()?.ToString();
        issue.Investigate(userId);
        await reconciliationRepository.SaveChangesAsync();
        return Ok(new { issue.Id, issue.Resolution });
    }

    [HttpPost("issues/{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolveIssueRequest request)
    {
        if (this.EnforceOperatorOr403() is { } block) return block;
        var tenantId = HttpContext.GetTenantId();
        var issue = await reconciliationRepository.GetIssueByIdAsync(tenantId, id);
        if (issue is null)
            return NotFound();

        if (issue.Resolution is "RESOLVED" or "DISMISSED")
            return BadRequest($"Issue is already {issue.Resolution}");

        var userId = HttpContext.GetCurrentUserId()?.ToString();
        issue.Resolve(request.Notes, userId);
        await reconciliationRepository.SaveChangesAsync();
        return Ok(new { issue.Id, issue.Resolution, issue.ResolvedBy, issue.ResolvedAt });
    }

    [HttpPost("issues/{id:guid}/dismiss")]
    public async Task<IActionResult> Dismiss(Guid id, [FromBody] ResolveIssueRequest request)
    {
        if (this.EnforceOperatorOr403() is { } block) return block;
        var tenantId = HttpContext.GetTenantId();
        var issue = await reconciliationRepository.GetIssueByIdAsync(tenantId, id);
        if (issue is null)
            return NotFound();

        if (issue.Resolution is "RESOLVED" or "DISMISSED")
            return BadRequest($"Issue is already {issue.Resolution}");

        var userId = HttpContext.GetCurrentUserId()?.ToString();
        issue.Dismiss(request.Notes, userId);
        await reconciliationRepository.SaveChangesAsync();
        return Ok(new { issue.Id, issue.Resolution, issue.ResolvedBy, issue.ResolvedAt });
    }
}

public record ResolveIssueRequest(string Notes);
