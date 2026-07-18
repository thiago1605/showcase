using FellowCore.Domain.Enums;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FellowCore.Api.Metrics;

/// <summary>
/// Background service that periodically queries the database to update observable gauge metrics.
/// Runs every 30 seconds and updates: payout age, disputes, outbox DLQ, reconciliation issues,
/// ledger imbalance, Hangfire stats, and health check statuses.
/// </summary>
public sealed class MetricsCollectorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FellowCoreMetrics _metrics;
    private readonly ILogger<MetricsCollectorWorker> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public MetricsCollectorWorker(
        IServiceScopeFactory scopeFactory,
        FellowCoreMetrics metrics,
        ILogger<MetricsCollectorWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectMetricsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[METRICS] Error collecting metrics");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task CollectMetricsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Oldest pending payout age
        var oldestPendingPayout = await db.Payouts
            .Where(p => p.Status == PayoutStatus.PENDING || p.Status == PayoutStatus.PROCESSING)
            .OrderBy(p => p.CreatedAt)
            .Select(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (oldestPendingPayout != default)
        {
            var ageHours = (DateTime.UtcNow - oldestPendingPayout).TotalHours;
            _metrics.SetPayoutsPendingAgeHours(ageHours);
        }
        else
        {
            _metrics.SetPayoutsPendingAgeHours(0);
        }

        // Open disputes count
        var openDisputes = await db.Disputes
            .CountAsync(d => d.Status == DisputeStatus.OPEN, ct);
        _metrics.SetDisputesOpenCount(openDisputes);

        // Outbox dead letter count
        var dlqCount = await db.OutboxMessages
            .CountAsync(m => m.Error != null && m.Error.StartsWith("[DLQ]"), ct);
        _metrics.SetOutboxDeadLetterCount(dlqCount);

        // Reconciliation issues (open, by severity)
        var issues = await db.ReconciliationIssues
            .Where(i => i.ResolvedAt == null)
            .GroupBy(i => i.Severity)
            .Select(g => new { Severity = g.Key, Count = g.LongCount() })
            .ToListAsync(ct);

        _metrics.SetReconciliationIssues("CRITICAL", 0);
        _metrics.SetReconciliationIssues("WARNING", 0);
        foreach (var issue in issues)
        {
            _metrics.SetReconciliationIssues(issue.Severity, issue.Count);
        }

        // Ledger global imbalance (sum of all entries should be 0 in double-entry)
        var netSum = await db.LedgerEntries.SumAsync(e => e.Amount, ct);
        var imbalanceCents = (double)(netSum * 100m);
        _metrics.SetLedgerGlobalImbalanceCents(imbalanceCents);

        // ADVANCE: reserve agregada (sum tenants) + per-seller exposure
        var reserveRemainingCents = await db.TenantConfigs.SumAsync(c => c.PlatformAdvanceReserveCents, ct);
        _metrics.SetAdvanceReserveValues(reserveRemainingCents, reserveRemainingCents);

        // Default threshold global (fallback quando seller não tem override).
        // Lido via IConfiguration scoped — caso de teste vale fixar pelo appsettings.
        var configuration = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        long defaultThresholdCents = configuration.GetValue("AdvanceAlert:DefaultSellerExposureThresholdCents", 100000L);

        // Per-seller exposure + threshold — só sellers com exposure > 0 mantém cardinalidade baixa.
        var sellerExposures = await db.Sellers
            .Where(s => s.AdvanceExposureCurrent > 0)
            .Select(s => new { s.Id, Exposure = s.AdvanceExposureCurrent, CustomThreshold = s.AdvanceExposureAlertThresholdCents })
            .ToListAsync(ct);
        foreach (var s in sellerExposures)
        {
            _metrics.SetSellerExposure(s.Id.ToString(), (long)(s.Exposure * 100m));
            _metrics.SetSellerExposureThreshold(s.Id.ToString(), s.CustomThreshold ?? defaultThresholdCents);
        }

        // Health check statuses
        var healthCheckService = scope.ServiceProvider.GetService<HealthCheckService>();
        if (healthCheckService is not null)
        {
            var report = await healthCheckService.CheckHealthAsync(ct);
            foreach (var entry in report.Entries)
            {
                _metrics.SetHealthCheckStatus(entry.Key, entry.Value.Status == HealthStatus.Healthy);
            }
        }
    }
}
