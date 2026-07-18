using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Common.Utils;
using FellowCore.Application.Modules.Notifications.DTOs;
using FellowCore.Application.Modules.Notifications.Interfaces;
using FellowCore.Domain.Interfaces;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public class NotificationsProcessor(
    ISellerRepository sellerRepository,
    ISecurityService securityService,
    IHttpClientFactory httpClientFactory,
    ILogger<NotificationsProcessor> logger) : INotificationsProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const int MaxRetries = 5;

    [AutomaticRetry(Attempts = MaxRetries, DelaysInSeconds = [10, 30, 60, 300, 900])]
    public async Task ProcessAsync(NotificationJobData job)
    {
        logger.LogInformation("Iniciando notificacao para Seller {SellerId}, Transaction {TransactionId}",
            job.SellerId, job.TransactionId);

        var seller = await sellerRepository.GetByIdAsync(tenantId: job.TenantId, sellerId: job.SellerId);

        if (seller == null || string.IsNullOrWhiteSpace(seller.WebhookUrl))
        {
            logger.LogWarning("Seller {SellerId} nao tem URL de webhook configurada. Ignorando.", job.SellerId);
            return;
        }

        string eventType = $"transaction.{job.Status.ToString().ToLowerInvariant()}";

        var payload = new SellerWebhookPayload(
            Event: eventType,
            Data: new SellerWebhookData(
                Id: job.TransactionId,
                ProviderId: job.ProviderTxId,
                Status: job.Status.ToString(),
                Amount: job.NetAmount,
                Type: job.PaymentType.ToString(),
                UpdatedAt: DateTime.UtcNow
            )
        );

        var sw = Stopwatch.StartNew();
        int? responseCode = null;

        try
        {
            using var client = httpClientFactory.CreateClient("WebhookClient");
            client.Timeout = TimeSpan.FromSeconds(10);

            string jsonPayload = JsonSerializer.Serialize(payload, JsonOptions);
            string webhookSecret = await securityService.DecryptAsync(seller.WebhookSecret);
            string signature = CryptoUtils.GenerateHmacSha256(jsonPayload, webhookSecret);

            client.DefaultRequestHeaders.Add("User-Agent", "FellowCoreGateway/1.0");
            client.DefaultRequestHeaders.Add("X-Signature", signature);
            client.DefaultRequestHeaders.Add("X-Webhook-Event", eventType);

            var response = await client.PostAsJsonAsync(seller.WebhookUrl, payload);
            responseCode = (int)response.StatusCode;
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Seller webhook entregue. Seller: {SellerId} | Url: {Url} | Event: {EventType} | Transaction: {TransactionId} | Status: {StatusCode} | Duration: {DurationMs}ms",
                    job.SellerId, seller.WebhookUrl, eventType, job.TransactionId, responseCode, sw.ElapsedMilliseconds);
            }
            else
            {
                logger.LogWarning(
                    "Seller webhook falhou. Seller: {SellerId} | Url: {Url} | Event: {EventType} | Transaction: {TransactionId} | Status: {StatusCode} | Duration: {DurationMs}ms",
                    job.SellerId, seller.WebhookUrl, eventType, job.TransactionId, responseCode, sw.ElapsedMilliseconds);
                throw new HttpRequestException($"Webhook delivery failed with status {responseCode}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            sw.Stop();
            logger.LogError(ex,
                "Seller webhook erro. Seller: {SellerId} | Url: {Url} | Event: {EventType} | Transaction: {TransactionId} | ResponseCode: {StatusCode} | Duration: {DurationMs}ms | Error: {Error}",
                job.SellerId, seller.WebhookUrl, eventType, job.TransactionId, responseCode, sw.ElapsedMilliseconds, ex.Message);
            throw; // Hangfire will retry with exponential backoff
        }
    }
}
