using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

/// <summary>
/// DB3: Data retention processor. Purges old operational data based on retention policy.
/// Runs as a Hangfire recurring job (weekly).
///
/// Retention policy:
///   - Webhook deliveries: 90 days
///   - Outbox messages (processed): 30 days
///   - Idempotency keys (Redis): handled by Redis TTL (24h)
///   - Login logs: 180 days
///   - Reconciliation runs/issues: 365 days
///   - Audit trail (ledger_entries, transactions): NEVER deleted (regulatory)
/// </summary>
public class DataRetentionProcessor(
    AppDbContext dbContext,
    ILogger<DataRetentionProcessor> logger)
{
    public async Task ProcessAsync(CancellationToken ct = default)
    {
        logger.LogInformation("[DATA_RETENTION] Starting data retention cleanup...");

        int totalDeleted = 0;

        // 1. Webhook deliveries older than 90 days (keep recent for debugging)
        var webhookCutoff = DateTime.UtcNow.AddDays(-90);
        int webhookDeleted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM webhook_deliveries WHERE created_at < {webhookCutoff} AND status IN ('SUCCEEDED', 'FAILED')", ct);
        totalDeleted += webhookDeleted;
        logger.LogInformation("[DATA_RETENTION] Purged {Count} webhook deliveries older than 90 days", webhookDeleted);

        // 2. Outbox messages already processed, older than 30 days
        var outboxCutoff = DateTime.UtcNow.AddDays(-30);
        int outboxDeleted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM outbox_messages WHERE processed_at IS NOT NULL AND processed_at < {outboxCutoff}", ct);
        totalDeleted += outboxDeleted;
        logger.LogInformation("[DATA_RETENTION] Purged {Count} processed outbox messages older than 30 days", outboxDeleted);

        // 3. Login logs older than 180 days
        var loginCutoff = DateTime.UtcNow.AddDays(-180);
        int loginDeleted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM login_logs WHERE created_at < {loginCutoff}", ct);
        totalDeleted += loginDeleted;
        logger.LogInformation("[DATA_RETENTION] Purged {Count} login logs older than 180 days", loginDeleted);

        // 4. Reconciliation runs/issues older than 365 days (keep 1 year for auditing)
        var reconCutoff = DateTime.UtcNow.AddDays(-365);
        int reconIssuesDeleted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM reconciliation_issues WHERE created_at < {reconCutoff}", ct);
        int reconRunsDeleted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM reconciliation_runs WHERE created_at < {reconCutoff}", ct);
        totalDeleted += reconIssuesDeleted + reconRunsDeleted;
        logger.LogInformation("[DATA_RETENTION] Purged {Issues} reconciliation issues and {Runs} runs older than 365 days",
            reconIssuesDeleted, reconRunsDeleted);

        // 5. Settlement report items older than 365 days
        int settlementItemsDeleted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM settlement_report_items WHERE created_at < {reconCutoff}", ct);
        int settlementReportsDeleted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM settlement_reports WHERE created_at < {reconCutoff}", ct);
        totalDeleted += settlementItemsDeleted + settlementReportsDeleted;
        logger.LogInformation("[DATA_RETENTION] Purged {Items} settlement items and {Reports} reports older than 365 days",
            settlementItemsDeleted, settlementReportsDeleted);

        logger.LogInformation("[DATA_RETENTION] Cleanup complete. Total rows deleted: {Total}", totalDeleted);
    }
}
