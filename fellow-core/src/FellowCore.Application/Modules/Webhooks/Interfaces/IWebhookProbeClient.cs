using FellowCore.Application.Modules.Webhooks.DTOs;

namespace FellowCore.Application.Modules.Webhooks.Interfaces;

/// <summary>
/// Cliente HTTP que envia um payload sintético assinado pra um endpoint do seller
/// e devolve o resultado bruto (status, latência, body). Implementação fica em
/// Infrastructure pra Application não falar HTTP direto.
/// </summary>
public interface IWebhookProbeClient
{
    Task<WebhookTestResultDto> ProbeAsync(string url, string secret, string eventType, CancellationToken ct = default);
}
