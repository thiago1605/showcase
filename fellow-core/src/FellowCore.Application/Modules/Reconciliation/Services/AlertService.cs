using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Reconciliation.Services;

public class AlertService(
    IEmailService emailService,
    ITenantRepository tenantRepository,
    IConfiguration configuration,
    ILogger<AlertService> logger) : IAlertService
{
    public async Task SendCriticalAlertAsync(ReconciliationIssue issue, CancellationToken ct = default)
    {
        // Always log immediately
        logger.LogCritical(
            "[ALERT:CRITICAL] TenantId={TenantId} Type={Type} InternalId={InternalId} ExternalId={ExternalId} " +
            "Expected={Expected} Actual={Actual} Drift={Drift} — {Description}",
            issue.TenantId, issue.Type, issue.InternalId, issue.ExternalId,
            issue.ExpectedCents, issue.ActualCents, issue.DriftCents, issue.Description);

        // Email if configured
        string? alertEmail = configuration["Reconciliation:AlertEmail"];
        if (!string.IsNullOrEmpty(alertEmail))
        {
            var tenant = await tenantRepository.GetByIdWithConfigAsync(issue.TenantId);
            string tenantName = tenant?.Name ?? issue.TenantId.ToString();

            var message = new EmailMessage(
                To: alertEmail,
                ToName: "FellowCore Ops",
                Subject: $"[CRITICAL] Reconciliation issue — {issue.Type} — {tenantName}",
                HtmlBody: BuildCriticalEmailBody(issue, tenantName));

            await emailService.SendAsync(message, ct);
        }
    }

    public async Task SendWarningDigestAsync(IReadOnlyList<ReconciliationIssue> issues, CancellationToken ct = default)
    {
        if (issues.Count == 0) return;

        logger.LogWarning(
            "[ALERT:DIGEST] {Count} warning issues pending across {Tenants} tenants",
            issues.Count, issues.Select(i => i.TenantId).Distinct().Count());

        string? alertEmail = configuration["Reconciliation:AlertEmail"];
        if (string.IsNullOrEmpty(alertEmail)) return;

        var message = new EmailMessage(
            To: alertEmail,
            ToName: "FellowCore Ops",
            Subject: $"[WARNING] Reconciliation digest — {issues.Count} issues",
            HtmlBody: BuildDigestEmailBody(issues));

        await emailService.SendAsync(message, ct);
    }

    private static string BuildCriticalEmailBody(ReconciliationIssue issue, string tenantName)
    {
        return $"""
        <div style="font-family:monospace;max-width:600px;margin:0 auto;padding:20px">
            <h2 style="color:#dc2626">CRITICAL Reconciliation Issue</h2>
            <table style="width:100%;border-collapse:collapse">
                <tr><td style="padding:4px 8px;font-weight:bold">Tenant</td><td>{tenantName}</td></tr>
                <tr><td style="padding:4px 8px;font-weight:bold">Type</td><td>{issue.Type}</td></tr>
                <tr><td style="padding:4px 8px;font-weight:bold">Internal ID</td><td>{issue.InternalId ?? "—"}</td></tr>
                <tr><td style="padding:4px 8px;font-weight:bold">External ID</td><td>{issue.ExternalId ?? "—"}</td></tr>
                <tr><td style="padding:4px 8px;font-weight:bold">Expected</td><td>{issue.ExpectedCents} cents</td></tr>
                <tr><td style="padding:4px 8px;font-weight:bold">Actual</td><td>{issue.ActualCents} cents</td></tr>
                <tr><td style="padding:4px 8px;font-weight:bold">Drift</td><td style="color:#dc2626;font-weight:bold">{issue.DriftCents} cents</td></tr>
                <tr><td style="padding:4px 8px;font-weight:bold">Description</td><td>{issue.Description}</td></tr>
                <tr><td style="padding:4px 8px;font-weight:bold">Detected At</td><td>{issue.CreatedAt:u}</td></tr>
            </table>
            <p style="margin-top:16px;color:#666">This issue requires immediate investigation.</p>
        </div>
        """;
    }

    private static string BuildDigestEmailBody(IReadOnlyList<ReconciliationIssue> issues)
    {
        var rows = string.Join("\n", issues.Select(i =>
            $"<tr><td style='padding:4px 8px'>{i.Type}</td>" +
            $"<td style='padding:4px 8px'>{i.TenantId.ToString()[..8]}…</td>" +
            $"<td style='padding:4px 8px'>{i.InternalId ?? "—"}</td>" +
            $"<td style='padding:4px 8px'>{i.DriftCents}</td>" +
            $"<td style='padding:4px 8px'>{i.CreatedAt:u}</td></tr>"));

        return $"""
        <div style="font-family:monospace;max-width:800px;margin:0 auto;padding:20px">
            <h2 style="color:#f59e0b">Reconciliation Warning Digest</h2>
            <p>{issues.Count} warning-level issues detected.</p>
            <table style="width:100%;border-collapse:collapse;border:1px solid #ddd">
                <thead><tr style="background:#f3f4f6">
                    <th style="padding:8px;text-align:left">Type</th>
                    <th style="padding:8px;text-align:left">Tenant</th>
                    <th style="padding:8px;text-align:left">Internal ID</th>
                    <th style="padding:8px;text-align:left">Drift</th>
                    <th style="padding:8px;text-align:left">Detected</th>
                </tr></thead>
                <tbody>{rows}</tbody>
            </table>
        </div>
        """;
    }
}
