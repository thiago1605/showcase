using System.Text.Json.Serialization;

namespace FellowCore.Application.Modules.Webhooks.DTOs;

public record StripeWebhookDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("data")] StripeWebhookData Data,
    [property: JsonPropertyName("account")] string? Account = null
);

public record StripeWebhookData(
    [property: JsonPropertyName("object")] StripeWebhookObject Object
);

public record StripeWebhookObject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("amount")] long? Amount = null,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata = null,
    [property: JsonPropertyName("charges")] StripeChargeList? Charges = null,
    // charge.refunded: the Charge object includes payment_intent and amount_refunded
    [property: JsonPropertyName("payment_intent")] string? PaymentIntent = null,
    [property: JsonPropertyName("amount_refunded")] long? AmountRefunded = null,
    // charge.dispute.created/closed: dispute object fields
    [property: JsonPropertyName("charge")] string? Charge = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    // account.updated fields (Custom Connected Accounts)
    [property: JsonPropertyName("charges_enabled")] bool? ChargesEnabled = null,
    [property: JsonPropertyName("payouts_enabled")] bool? PayoutsEnabled = null,
    [property: JsonPropertyName("requirements")] StripeAccountRequirements? Requirements = null
);

public record StripeAccountRequirements(
    [property: JsonPropertyName("currently_due")] List<string>? CurrentlyDue = null,
    [property: JsonPropertyName("eventually_due")] List<string>? EventuallyDue = null,
    [property: JsonPropertyName("disabled_reason")] string? DisabledReason = null
);

public record StripeChargeList(
    [property: JsonPropertyName("data")] List<StripeChargeData>? Data = null
);

public record StripeChargeData(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("payment_method_details")] StripePaymentMethodDetails? PaymentMethodDetails = null
);

public record StripePaymentMethodDetails(
    [property: JsonPropertyName("card")] StripeCardDetails? Card = null
);

public record StripeCardDetails(
    [property: JsonPropertyName("brand")] string? Brand = null,
    [property: JsonPropertyName("last4")] string? Last4 = null,
    [property: JsonPropertyName("wallet")] StripeWalletDetails? Wallet = null
);

public record StripeWalletDetails(
    [property: JsonPropertyName("type")] string? Type = null
);
