using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FellowCore.Api.HealthChecks;

/// <summary>
/// Verifies Stripe API connectivity by calling the /v1/balance endpoint.
/// Tagged "external" so it can be queried separately from core infrastructure health.
/// </summary>
public class StripeHealthCheck(IStripeApiClient stripeApiClient, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var apiKey = configuration["Stripe:SecretKey"];
            if (string.IsNullOrEmpty(apiKey))
                return HealthCheckResult.Degraded("Stripe:SecretKey is not configured.");

            await stripeApiClient.GetBalanceAsync(apiKey);
            return HealthCheckResult.Healthy("Stripe API is reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Stripe API is unreachable.", ex);
        }
    }
}
