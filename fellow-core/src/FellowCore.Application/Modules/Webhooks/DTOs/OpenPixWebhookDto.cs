using System.Text.Json.Serialization;

namespace FellowCore.Application.Modules.Webhooks.DTOs;

public record OpenPixWebhookDto(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("charge")] OpenPixWebhookCharge? Charge,
    [property: JsonPropertyName("pix")] OpenPixWebhookPix? Pix,
    [property: JsonPropertyName("accountRegister")] OpenPixWebhookAccountRegister? AccountRegister = null
);

public record OpenPixWebhookCharge(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("correlationID")] string CorrelationId,
    [property: JsonPropertyName("transactionID")] string? TransactionId,
    [property: JsonPropertyName("brCode")] string? BrCode,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("updatedAt")] string? UpdatedAt,
    [property: JsonPropertyName("customer")] OpenPixWebhookCustomer? Customer = null
);

public record OpenPixWebhookPix(
    [property: JsonPropertyName("value")] int Value,
    [property: JsonPropertyName("transactionID")] string TransactionId,
    [property: JsonPropertyName("time")] string? Time = null
);

public record OpenPixWebhookCustomer(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("taxID")] OpenPixWebhookTaxId? TaxId = null
);

public record OpenPixWebhookTaxId(
    [property: JsonPropertyName("taxID")] string? Value,
    [property: JsonPropertyName("type")] string? Type
);

public record OpenPixWebhookAccountRegister(
    [property: JsonPropertyName("correlationID")] string CorrelationId,
    [property: JsonPropertyName("status")] string Status
);
