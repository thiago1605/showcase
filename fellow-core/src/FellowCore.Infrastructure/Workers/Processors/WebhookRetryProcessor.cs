using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Common.Utils;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public class WebhookRetryProcessor(
    IWebhookDeliveryRepository deliveryRepository,
    ISecurityService securityService,
    IHttpClientFactory httpClientFactory,
    IEmailService emailService,
    IConfiguration configuration,
    ILogger<WebhookRetryProcessor> logger) : IWebhookRetryProcessor
{
    public async Task ProcessPendingRetriesAsync(CancellationToken ct = default)
    {
        var pendingDeliveries = await deliveryRepository.GetPendingRetriesAsync(DateTime.UtcNow);

        if (pendingDeliveries.Count == 0)
            return;

        logger.LogInformation("Processando {Count} webhook(s) pendente(s) de retry", pendingDeliveries.Count);

        foreach (var delivery in pendingDeliveries)
        {
            if (ct.IsCancellationRequested) break;

            var endpoint = delivery.Endpoint;
            if (endpoint == null || !endpoint.Enabled)
            {
                delivery.RecordRetryAttempt(null, false, 0, "Endpoint desabilitado ou removido");
                continue;
            }

            var sw = Stopwatch.StartNew();
            int? responseCode = null;
            bool success = false;
            string? error = null;

            try
            {
                using var client = httpClientFactory.CreateClient("WebhookClient");
                client.Timeout = TimeSpan.FromSeconds(10);

                string jsonPayload = delivery.Payload.RootElement.GetRawText();
                string decryptedSecret = await securityService.DecryptAsync(endpoint.Secret);
                string signature = CryptoUtils.GenerateHmacSha256(jsonPayload, decryptedSecret);

                var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
                {
                    Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Webhook-Signature", signature);
                request.Headers.Add("X-Webhook-Event", delivery.EventType);
                request.Headers.Add("User-Agent", "FellowCoreGateway/1.0");

                var response = await client.SendAsync(request, ct);
                responseCode = (int)response.StatusCode;
                success = response.IsSuccessStatusCode;

                if (!success)
                    error = $"HTTP {responseCode}";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                logger.LogWarning(ex, "Retry {Attempt} falhou para delivery {DeliveryId} -> {Url}",
                    delivery.RetryCount + 1, delivery.Id, endpoint.Url);
            }
            finally
            {
                sw.Stop();
                delivery.RecordRetryAttempt(responseCode, success, (int)sw.ElapsedMilliseconds, error);

                if (success)
                    logger.LogInformation("Retry bem-sucedido para delivery {DeliveryId} na tentativa {Attempt}",
                        delivery.Id, delivery.RetryCount);
                else if (!delivery.CanRetry)
                {
                    logger.LogCritical(
                        "DLQ: Delivery {DeliveryId} esgotou {MaxRetries} tentativas. Endpoint: {Url} | EventType: {EventType} | EventId: {EventId} | LastError: {LastError}. Requer investigação manual.",
                        delivery.Id, WebhookDelivery.MaxRetryAttempts,
                        endpoint.Url, delivery.EventType, delivery.EventId, delivery.LastError);

                    await SendDlqAlertAsync(delivery, endpoint.Url);
                }
            }
        }

        await deliveryRepository.SaveChangesAsync();
    }

    private async Task SendDlqAlertAsync(WebhookDelivery delivery, string endpointUrl)
    {
        string? alertEmail = configuration["Reconciliation:AlertEmail"];
        if (string.IsNullOrEmpty(alertEmail)) return;

        try
        {
            var message = new EmailMessage(
                To: alertEmail,
                ToName: "FellowCore Ops",
                Subject: $"[DLQ] Webhook delivery failed — {delivery.EventType}",
                HtmlBody: $"""
                <div style="font-family:monospace;max-width:600px;margin:0 auto;padding:20px">
                    <h2 style="color:#dc2626">Webhook Delivery Exhausted Retries</h2>
                    <table style="width:100%;border-collapse:collapse">
                        <tr><td style="padding:4px 8px;font-weight:bold">Delivery ID</td><td>{delivery.Id}</td></tr>
                        <tr><td style="padding:4px 8px;font-weight:bold">Event ID</td><td>{delivery.EventId}</td></tr>
                        <tr><td style="padding:4px 8px;font-weight:bold">Event Type</td><td>{delivery.EventType}</td></tr>
                        <tr><td style="padding:4px 8px;font-weight:bold">Endpoint</td><td>{endpointUrl}</td></tr>
                        <tr><td style="padding:4px 8px;font-weight:bold">Retries</td><td>{delivery.RetryCount}/{WebhookDelivery.MaxRetryAttempts}</td></tr>
                        <tr><td style="padding:4px 8px;font-weight:bold">Last Error</td><td style="color:#dc2626">{delivery.LastError}</td></tr>
                        <tr><td style="padding:4px 8px;font-weight:bold">Created At</td><td>{delivery.CreatedAt:u}</td></tr>
                    </table>
                    <p style="margin-top:16px;color:#666">This delivery has been moved to the dead letter queue. Manual intervention required.</p>
                </div>
                """);

            await emailService.SendAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send DLQ alert email for delivery {DeliveryId}", delivery.Id);
        }
    }
}
