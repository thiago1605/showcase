using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FellowCore.Api.HealthChecks;

/// <summary>
/// Verifies OpenPix API connectivity by calling the static QR list endpoint (lightweight).
/// Tagged "external" so it can be queried separately from core infrastructure health.
/// </summary>
public class OpenPixHealthCheck(IOpenPixApiClient openPixApiClient, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var appId = configuration["OpenPix:AppId"];
            if (string.IsNullOrEmpty(appId))
                return HealthCheckResult.Degraded("OpenPix:AppId is not configured.");

            await openPixApiClient.ListStaticQrAsync(appId);
            return HealthCheckResult.Healthy("OpenPix API is reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("OpenPix API is unreachable.", ex);
        }
    }
}
