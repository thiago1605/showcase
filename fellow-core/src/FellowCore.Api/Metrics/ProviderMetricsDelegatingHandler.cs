using System.Diagnostics;

namespace FellowCore.Api.Metrics;

/// <summary>
/// HttpClient delegating handler that automatically records provider request metrics
/// (total requests, errors, duration) for all outgoing HTTP calls to payment providers.
/// </summary>
public sealed class ProviderMetricsDelegatingHandler(FellowCoreMetrics metrics, string providerName) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        metrics.RecordProviderRequest(providerName);
        var sw = Stopwatch.StartNew();

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            sw.Stop();

            metrics.RecordProviderRequestDuration(sw.Elapsed.TotalSeconds, providerName, request.RequestUri?.AbsolutePath ?? "unknown");

            if (!response.IsSuccessStatusCode)
            {
                var errorType = (int)response.StatusCode >= 500 ? "server_error" : "client_error";
                metrics.RecordProviderError(providerName, errorType);
            }

            return response;
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            metrics.RecordProviderError(providerName, "timeout");
            metrics.RecordProviderRequestDuration(sw.Elapsed.TotalSeconds, providerName, request.RequestUri?.AbsolutePath ?? "unknown");
            throw;
        }
        catch (HttpRequestException)
        {
            sw.Stop();
            metrics.RecordProviderError(providerName, "connection_error");
            metrics.RecordProviderRequestDuration(sw.Elapsed.TotalSeconds, providerName, request.RequestUri?.AbsolutePath ?? "unknown");
            throw;
        }
    }
}
