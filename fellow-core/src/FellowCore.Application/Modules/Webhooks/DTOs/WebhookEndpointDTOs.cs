using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Webhooks.DTOs;

public record CreateWebhookEndpointDto(
    string Url,
    string Secret,
    List<string>? Events = null
);

public record WebhookEndpointResponseDto(
    Guid Id,
    string Url,
    List<string> Events,
    bool Enabled,
    DateTime CreatedAt,
    /// <summary>
    /// Quando setado, o endpoint é producer-scoped (dispara só pros eventos
    /// do seller correspondente). Quando null, é tenant-wide (legado — devs
    /// da plataforma recebem TODOS os eventos do tenant).
    /// </summary>
    Guid? SellerId = null
);

public record DeadLetterSummaryDto(
    int TotalCount,
    List<WebhookDeliveryResponseDto> Items
);

public record TestWebhookEndpointDto(string EventType = "webhook.test");

/// <summary>
/// Resultado da rotação de secret. Secret é o **único** ponto onde o seller
/// recebe o valor em claro — depois disso, fica só criptografado no DB.
/// </summary>
public record RotateWebhookSecretResultDto(string Secret);

/// <summary>
/// Resultado do envio sintético de webhook. StatusCode = código HTTP de resposta
/// do endpoint do seller (0 quando nem chegou ao remoto). Success = StatusCode em [200,299].
/// LatencyMs total da chamada incluindo connect. Error detalhado quando algo deu errado
/// (timeout, DNS, TLS, etc.).
/// </summary>
public record WebhookTestResultDto(
    bool Success,
    int StatusCode,
    long LatencyMs,
    string? ResponseBody,
    string? Error
);

public record WebhookDeliveryResponseDto(
    Guid Id,
    string EventId,
    string EventType,
    int? ResponseCode,
    bool Success,
    int Duration,
    DeliveryStatus Status,
    int RetryCount,
    string? LastError,
    DateTime CreatedAt
);
