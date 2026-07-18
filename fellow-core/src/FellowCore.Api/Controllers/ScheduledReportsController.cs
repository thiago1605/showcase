using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Reports.DTOs;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/scheduled-reports")]
[ApiKeyAuth]
[EnableRateLimiting("fixed")]
public class ScheduledReportsController(IScheduledReportRepository reportRepository) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateScheduledReportDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var report = ScheduledReport.Create(tenantId, request.ReportType, request.Format, request.Frequency, request.Recipients);
        reportRepository.Add(report);
        await reportRepository.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = report.Id }, MapToResponse(report));
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List()
    {
        Guid tenantId = HttpContext.GetTenantId();
        var reports = await reportRepository.GetByTenantAsync(tenantId);
        return Ok(reports.Select(MapToResponse));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var report = await reportRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("ScheduledReport.NotFound", $"Relatório {id} não encontrado.");
        return Ok(MapToResponse(report));
    }

    [HttpPost("{id:guid}/disable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disable(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var report = await reportRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("ScheduledReport.NotFound", $"Relatório {id} não encontrado.");
        report.Disable();
        reportRepository.Update(report);
        await reportRepository.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/enable")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Enable(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var report = await reportRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("ScheduledReport.NotFound", $"Relatório {id} não encontrado.");
        report.Enable();
        reportRepository.Update(report);
        await reportRepository.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var report = await reportRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("ScheduledReport.NotFound", $"Relatório {id} não encontrado.");
        report.Disable();
        reportRepository.Update(report);
        await reportRepository.SaveChangesAsync();
        return NoContent();
    }

    private static ScheduledReportResponse MapToResponse(ScheduledReport r) => new(
        r.Id, r.ReportType, r.Format, r.Frequency, r.Recipients,
        r.Enabled, r.LastSentAt, r.NextRunAt, r.CreatedAt);
}
