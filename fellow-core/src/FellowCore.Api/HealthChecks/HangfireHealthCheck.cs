using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FellowCore.Api.HealthChecks;

/// <summary>
/// Verifies Hangfire worker health by checking that recurring jobs exist and
/// none have a last execution older than 2 hours (stale).
/// Tagged "worker" so it can be queried separately from infrastructure/external health.
/// </summary>
public class HangfireHealthCheck : IHealthCheck
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(2);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = JobStorage.Current.GetConnection();
            var recurringJobs = connection.GetRecurringJobs();

            if (recurringJobs.Count == 0)
                return Task.FromResult(HealthCheckResult.Degraded("No recurring jobs found in Hangfire."));

            var staleJobs = recurringJobs
                .Where(j => j.LastExecution.HasValue && DateTime.UtcNow - j.LastExecution.Value > StaleThreshold)
                .Select(j => j.Id)
                .ToList();

            if (staleJobs.Count > 0)
            {
                return Task.FromResult(HealthCheckResult.Degraded(
                    $"Stale recurring jobs detected (last run > {StaleThreshold.TotalHours}h): {string.Join(", ", staleJobs)}"));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"{recurringJobs.Count} recurring jobs registered and active."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Failed to query Hangfire job storage.", ex));
        }
    }
}
