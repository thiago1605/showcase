using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FellowCore.Application.Common.Utils;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Webhooks;

/// <summary>
/// Implementação de IWebhookProbeClient — envia POST com payload sintético assinado
/// (HMAC-SHA256 hex em X-Signature) e devolve status/latência/body. Timeout 5s.
/// Mesmo formato dos webhooks reais (ver NotificationsProcessor) pra não introduzir
/// um schema "de teste" diferente do que o seller vai receber em produção.
/// </summary>
public sealed class WebhookProbeClient(
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookProbeClient> logger) : IWebhookProbeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<WebhookTestResultDto> ProbeAsync(string url, string secret, string eventType, CancellationToken ct = default)
    {
        var payload = new
        {
            @event = eventType,
            verification = true,
            data = new
            {
                id = $"evt_test_{Guid.NewGuid():N}",
                createdAt = DateTime.UtcNow,
                message = "Payload sintético enviado pelo Fellow Pay para verificar a configuração do endpoint.",
            },
        };

        string jsonPayload = JsonSerializer.Serialize(payload, JsonOptions);
        string signature = CryptoUtils.GenerateHmacSha256(jsonPayload, secret);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = httpClientFactory.CreateClient("WebhookClient");
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FellowCoreGateway/1.0");
            client.DefaultRequestHeaders.Add("X-Signature", signature);
            client.DefaultRequestHeaders.Add("X-Webhook-Event", eventType);

            using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content, ct);
            sw.Stop();

            string? body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
                // Trunca pra não inflar UI/log com corpos enormes.
                if (body.Length > 500) body = body[..500] + "…";
            }
            catch
            {
                // Corpo inacessível não bloqueia a verificação.
            }

            return new WebhookTestResultDto(
                Success: response.IsSuccessStatusCode,
                StatusCode: (int)response.StatusCode,
                LatencyMs: sw.ElapsedMilliseconds,
                ResponseBody: body,
                Error: response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new WebhookTestResultDto(false, 0, sw.ElapsedMilliseconds, null, "Timeout: o endpoint não respondeu em 5 segundos.");
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            logger.LogDebug(ex, "Falha de rede ao testar webhook {Url}", url);
            return new WebhookTestResultDto(false, 0, sw.ElapsedMilliseconds, null, $"Erro de conexão: {ex.Message}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "Erro inesperado ao testar webhook {Url}", url);
            return new WebhookTestResultDto(false, 0, sw.ElapsedMilliseconds, null, $"Erro inesperado: {ex.Message}");
        }
    }
}
