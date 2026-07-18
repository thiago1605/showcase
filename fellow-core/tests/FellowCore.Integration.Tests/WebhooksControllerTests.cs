using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Infrastructure.Database;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FellowCore.Integration.Tests;

public class WebhooksControllerTests : IntegrationTestBase
{
    private const string StripeWebhookSecret = "whsec_test_secret_for_integration_tests";

    // -- Stripe: missing signature --

    [Fact]
    public async Task StripeWebhook_WithNoSignature_ReturnsUnauthorized()
    {
        var payload = BuildStripePayload();
        var content = JsonContent.Create(payload);

        // Do NOT add Stripe-Signature header
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe")
        {
            Content = content
        };

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -- Stripe: invalid signature --

    [Fact]
    public async Task StripeWebhook_WithInvalidSignature_ReturnsUnauthorized()
    {
        var payload = BuildStripePayload();
        var content = JsonContent.Create(payload);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe")
        {
            Content = content
        };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1=invalid_signature_value");

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -- Stripe: expired timestamp --

    [Fact]
    public async Task StripeWebhook_WithExpiredTimestamp_ReturnsUnauthorized()
    {
        var payload = BuildStripePayload();
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var jsonString = Encoding.UTF8.GetString(jsonBytes);

        // Use a timestamp that is more than 5 minutes old
        var expiredTimestamp = (DateTimeOffset.UtcNow.AddMinutes(-10)).ToUnixTimeSeconds().ToString();
        var signature = ComputeStripeSignature(jsonString, expiredTimestamp, StripeWebhookSecret);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe")
        {
            Content = new StringContent(jsonString, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", $"t={expiredTimestamp},v1={signature}");

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -- Stripe: valid signature returns 200 --

    [Fact]
    public async Task StripeWebhook_WithValidSignature_ReturnsOk()
    {
        var payload = BuildStripePayload();
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var jsonString = Encoding.UTF8.GetString(jsonBytes);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeStripeSignature(jsonString, timestamp, StripeWebhookSecret);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe")
        {
            Content = new StringContent(jsonString, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={signature}");

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("received").GetBoolean().Should().BeTrue();
    }

    // -- Stripe: malformed signature header (no v1 part) --

    [Fact]
    public async Task StripeWebhook_WithMalformedSignatureHeader_ReturnsUnauthorized()
    {
        var payload = BuildStripePayload();
        var content = JsonContent.Create(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe")
        {
            Content = content
        };
        request.Headers.Add("Stripe-Signature", "malformed-no-t-or-v1");

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -- OpenPix: missing Authorization header --

    [Fact]
    public async Task OpenPixWebhook_WithNoAuthToken_ReturnsUnauthorized()
    {
        var payload = BuildOpenPixPayload();
        var content = JsonContent.Create(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openpix")
        {
            Content = content
        };
        // Do NOT add Authorization header

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -- OpenPix: invalid auth token still passes filter (validation is deferred to service) --
    // The WebhookAuthFilter for OpenPix only checks for presence, not validity.
    // Any non-empty token is accepted at the filter level.

    [Fact]
    public async Task OpenPixWebhook_WithInvalidAuthToken_ReturnsOk()
    {
        var payload = BuildOpenPixPayload();
        var content = JsonContent.Create(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openpix")
        {
            Content = content
        };
        request.Headers.Add("Authorization", "invalid-token-value");

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);

        // Filter passes (token is present); handler returns immediately for unknown event with null charge
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -- OpenPix: valid token returns 200 --

    [Fact]
    public async Task OpenPixWebhook_WithValidToken_ReturnsOk()
    {
        var payload = BuildOpenPixPayload();
        var content = JsonContent.Create(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openpix")
        {
            Content = content
        };
        request.Headers.Add("Authorization", "fake-app-id-for-tests");

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("received").GetBoolean().Should().BeTrue();
    }

    // -- OpenPix: response body contains expected wrapper structure --

    [Fact]
    public async Task OpenPixWebhook_ResponseBody_HasStandardWrapper()
    {
        var payload = BuildOpenPixPayload();
        var content = JsonContent.Create(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openpix")
        {
            Content = content
        };
        request.Headers.Add("Authorization", "some-token");

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.TryGetProperty("success", out _).Should().BeTrue();
        json.TryGetProperty("data", out _).Should().BeTrue();
    }

    // -- Stripe: valid signature with known event type (payment_intent.succeeded, unknown tx) --

    [Fact]
    public async Task StripeWebhook_WithValidSignature_KnownEventType_ReturnsOk()
    {
        // Even with a known event type, the handler gracefully returns when the transaction
        // is not found in the database.
        var payload = BuildStripePayload(eventType: "payment_intent.succeeded");
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var jsonString = Encoding.UTF8.GetString(jsonBytes);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeStripeSignature(jsonString, timestamp, StripeWebhookSecret);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe")
        {
            Content = new StringContent(jsonString, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={signature}");

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -- Stripe: webhook endpoints do not require API key auth --

    [Fact]
    public async Task StripeWebhook_DoesNotRequireApiKeyAuth()
    {
        var payload = BuildStripePayload();
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var jsonString = Encoding.UTF8.GetString(jsonBytes);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeStripeSignature(jsonString, timestamp, StripeWebhookSecret);

        // Use unauthenticated client (no X-Api-Key header)
        var client = CreateUnauthenticatedClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe")
        {
            Content = new StringContent(jsonString, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={signature}");

        var response = await client.SendAsync(request);

        // Should NOT return 401 for missing API key — AllowAnonymous
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -- OpenPix: realistic token validation with seeded transaction --

    [Fact]
    public async Task OpenPixWebhook_InvalidToken_TransactionNotCaptured()
    {
        var (correlationId, _) = await SeedOpenPixTransactionAsync("valid-seller-token-1");

        var payload = BuildOpenPixChargePayload("OPENPIX:CHARGE_COMPLETED", correlationId);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openpix")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("Authorization", "wrong-token");

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Transaction must remain PROCESSING — invalid token silently rejected
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tx = await db.Transactions.AsNoTracking().FirstAsync(t => t.ProviderTxId == correlationId);
        tx.Status.Should().Be(TransactionStatus.PROCESSING);
    }

    [Fact]
    public async Task OpenPixWebhook_ValidSellerToken_TransactionCaptured()
    {
        const string sellerToken = "valid-seller-token-2";
        var (correlationId, _) = await SeedOpenPixTransactionAsync(sellerToken);

        var json = "{\"event\":\"OPENPIX:CHARGE_COMPLETED\",\"charge\":{\"status\":\"COMPLETED\",\"correlationID\":\"" + correlationId + "\"}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openpix")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", sellerToken);

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Transaction must be CAPTURED — valid seller token accepted
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tx = await db.Transactions.AsNoTracking().FirstAsync(t => t.ProviderTxId == correlationId);
        tx.Status.Should().Be(TransactionStatus.CAPTURED);
    }

    [Fact]
    public async Task OpenPixWebhook_ValidPlatformToken_TransactionCaptured()
    {
        // Seller has a different token, but the platform AppId should also work
        var (correlationId, _) = await SeedOpenPixTransactionAsync("some-other-seller-token");

        var json = "{\"event\":\"OPENPIX:CHARGE_COMPLETED\",\"charge\":{\"status\":\"COMPLETED\",\"correlationID\":\"" + correlationId + "\"}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/openpix")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        // Use platform AppId from config ("fake-app-id-for-tests")
        request.Headers.Add("Authorization", "fake-app-id-for-tests");

        var client = CreateUnauthenticatedClient();
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tx = await db.Transactions.AsNoTracking().FirstAsync(t => t.ProviderTxId == correlationId);
        tx.Status.Should().Be(TransactionStatus.CAPTURED);
    }

    /// <summary>
    /// Seeds an OPENPIX seller with encrypted access token, a WALLET ledger account,
    /// and a PROCESSING OPENPIX transaction. Returns (correlationId, sellerId).
    /// </summary>
    private async Task<(string CorrelationId, Guid SellerId)> SeedOpenPixTransactionAsync(string sellerToken)
    {
        var correlationId = Guid.NewGuid().ToString();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // FakeSecurityService.DecryptAsync("enc:X") → "X"
        var seller = Seller.Create(
            tenantId: TenantId,
            legalName: $"OpenPix Seller {correlationId[..8]}",
            document: "98765432000188",
            email: $"openpix-{correlationId[..8]}@test.com",
            webhookSecret: "test-webhook-secret-32chars-ok!!",
            preferredProvider: PaymentProvider.OPENPIX,
            externalAccountId: $"openpix-acc-{correlationId[..8]}",
            encryptedAccessToken: $"enc:{sellerToken}");

        var wallet = LedgerAccount.Create(TenantId, seller.Id, LedgerAccountType.WALLET);

        var txResult = Transaction.Create(
            tenantId: TenantId,
            amount: 100m,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.OPENPIX,
            installments: 1,
            feeAmount: 1.5m,
            netAmount: 98.5m,
            expectedSettlementDate: null,
            providerTxId: correlationId,
            sellerId: seller.Id);

        db.Sellers.Add(seller);
        db.LedgerAccounts.Add(wallet);
        db.Transactions.Add(txResult.Value);
        await db.SaveChangesAsync();

        return (correlationId, seller.Id);
    }

    private static OpenPixWebhookDto BuildOpenPixChargePayload(string eventType, string correlationId) =>
        new(
            Event: eventType,
            Charge: new OpenPixWebhookCharge("COMPLETED", correlationId, null, null, null, null),
            Pix: null);

    // -- Helpers --

    /// <summary>
    /// Builds a minimal Stripe webhook payload. Uses an unknown event type by default
    /// so the handler returns immediately without touching the database.
    /// </summary>
    private static StripeWebhookDto BuildStripePayload(string eventType = "test.event")
    {
        return new StripeWebhookDto(
            Id: $"evt_{Guid.NewGuid():N}",
            Type: eventType,
            Data: new StripeWebhookData(
                Object: new StripeWebhookObject(
                    Id: $"pi_{Guid.NewGuid():N}"
                )
            )
        );
    }

    /// <summary>
    /// Builds a minimal OpenPix webhook payload with no charge, so the handler
    /// returns immediately without database access.
    /// </summary>
    private static OpenPixWebhookDto BuildOpenPixPayload()
    {
        return new OpenPixWebhookDto(
            Event: "test_event",
            Charge: null,
            Pix: null
        );
    }

    /// <summary>
    /// Computes the HMAC-SHA256 signature in the same way the WebhookAuthFilter validates it.
    /// The signed payload is "{timestamp}.{body}" and the result is a lowercase hex string.
    /// </summary>
    private static string ComputeStripeSignature(string body, string timestamp, string secret)
    {
        var signedPayload = $"{timestamp}.{body}";
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(signedPayload);

        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hash).ToLower();
    }
}
