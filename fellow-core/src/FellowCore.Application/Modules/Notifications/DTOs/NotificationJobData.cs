using FellowCore.Domain.Enums;
using System.Text.Json.Serialization;

namespace FellowCore.Application.Modules.Notifications.DTOs;

public record NotificationJobData(
    Guid TenantId,
    Guid SellerId,
    Guid TransactionId,
    TransactionStatus Status,
    decimal NetAmount,
    string ProviderTxId,
    PaymentType PaymentType
);

public record SellerWebhookPayload(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("data")] SellerWebhookData Data
);

/// <summary>
/// Payload enriquecido enviado pros webhooks producer-scoped + tenant-wide.
/// Inclui dados de transação, cliente (do payer), produto (se TX é de marketplace)
/// e UTM/affiliate (se vieram em Metadata). Campos nullable porque nem toda TX
/// tem todos os dados (PIX wallet sem KYC pode não ter document, etc).
///
/// Formato camelCase pra integrar fácil com ferramentas de marketing automation
/// (ActiveCampaign, RD Station, Mailchimp todos usam JSON snake/camelCase).
/// </summary>
public record SellerWebhookData(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("provider_id")] string ProviderId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("updated_at")] DateTime UpdatedAt,

    // Producer webhook enrichment — opcionais; ficam null se a TX não tiver o dado.
    [property: JsonPropertyName("customer")] WebhookCustomerData? Customer = null,
    [property: JsonPropertyName("product")] WebhookProductData? Product = null,
    [property: JsonPropertyName("affiliate")] WebhookAffiliateData? Affiliate = null,
    [property: JsonPropertyName("utm")] WebhookUtmData? Utm = null,
    [property: JsonPropertyName("external_reference_id")] string? ExternalReferenceId = null
);

/// <summary>Dados do pagador — useful pra marketing automation (lista de leads, CRM).</summary>
public record WebhookCustomerData(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("document")] string? Document
);

/// <summary>Dados do produto vendido — apenas quando TX vem do marketplace (ExternalReferenceId = "product:{id}").</summary>
public record WebhookProductData(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("slug")] string? Slug
);

/// <summary>Dados do afiliado quando a venda veio atribuída a um. Null se não houver attribution.</summary>
public record WebhookAffiliateData(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string? Name
);

/// <summary>UTM tracking — extraído de Transaction.Metadata quando disponível.</summary>
public record WebhookUtmData(
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("medium")] string? Medium,
    [property: JsonPropertyName("campaign")] string? Campaign,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("term")] string? Term
);
