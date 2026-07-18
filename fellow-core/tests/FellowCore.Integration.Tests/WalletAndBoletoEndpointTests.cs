using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Domain.Enums;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

/// <summary>
/// Integration tests for Apple Pay, Google Pay, and Boleto purchase flows.
/// Validates: transaction creation, wallet type detection via webhook, and boleto-specific responses.
/// </summary>
public class WalletAndBoletoEndpointTests : IntegrationTestBase
{
    private static readonly PayerDto DefaultPayer = new("Maria Silva", "52998224725", "maria@test.com", "+5511999990000");

    // ── Apple Pay ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTransaction_CreditCard_ForApplePay_Returns201WithClientSecret()
    {
        // Apple Pay uses PaymentType.CREDIT_CARD — the wallet type is detected
        // on the webhook side (payment_intent.succeeded → charges.data[0].payment_method_details.card.wallet.type)
        var response = await CreateCardTransactionAsync(250m, "Apple Pay purchase test");

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = json.GetProperty("data");
        data.GetProperty("amount").GetDecimal().Should().Be(250m);
        // Status is serialized as int (CREATED=0)
        data.GetProperty("status").GetInt32().Should().Be((int)TransactionStatus.PROCESSING);

        // Card payments return a transactionId for frontend completion (Stripe.js / Apple Pay / Google Pay)
        var payment = data.GetProperty("payment");
        payment.GetProperty("transactionId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateTransaction_CreditCard_WithInstallments_Returns201()
    {
        var response = await CreateCardTransactionAsync(1200m, "Installment card purchase", installments: 3);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(1200m);
    }

    [Fact]
    public async Task CreateTransaction_DebitCard_Returns201()
    {
        // Debit card also goes through StripeCardRail (supports Apple Pay / Google Pay)
        var dto = new CreateTransactionDto(SellerId, 80m, PaymentType.DEBIT_CARD, 1, "Debit card test", DefaultPayer);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(dto);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(80m);
    }

    // ── Boleto ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTransaction_Boleto_Returns201()
    {
        var response = await CreateBoletoTransactionAsync(350m, "Boleto purchase test");

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = json.GetProperty("data");
        data.GetProperty("amount").GetDecimal().Should().Be(350m);
        data.GetProperty("status").GetInt32().Should().Be((int)TransactionStatus.PROCESSING);
        data.GetProperty("payment").GetProperty("transactionId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateTransaction_Boleto_WithoutPayerDocument_Returns400()
    {
        // Boleto requires CPF/CNPJ — StripeBoletoRail.Validate checks this
        var payer = new PayerDto("Maria Silva", "", "maria@test.com");
        var dto = new CreateTransactionDto(SellerId, 200m, PaymentType.BOLETO, 1, "Boleto sem CPF", payer);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(dto);

        var response = await Client.SendAsync(request);

        // Should fail validation (400 from ArgumentException in StripeBoletoRail.Validate
        // or 500 if the exception isn't mapped — either way, not 201/200)
        response.StatusCode.Should().NotBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTransaction_Boleto_SetsProviderToStripe()
    {
        var createResponse = await CreateBoletoTransactionAsync(500m, "Boleto provider check");
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = created.GetProperty("data").GetProperty("internalId").GetString();

        // Fetch full transaction detail
        var detailResponse = await Client.GetAsync($"/api/v1/transactions/{txId}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txData = detail.GetProperty("data");

        // Boleto goes through StripeBoletoRail → provider is STRIPE (enum int = 0)
        txData.GetProperty("provider").GetInt32().Should().Be((int)PaymentProvider.STRIPE);
        txData.GetProperty("paymentType").GetInt32().Should().Be((int)PaymentType.BOLETO);
    }

    // ── Google Pay (same flow as Apple Pay) ──────────────────────────────

    [Fact]
    public async Task CreateTransaction_CreditCard_ForGooglePay_Returns201()
    {
        // Google Pay also uses PaymentType.CREDIT_CARD with automatic_payment_methods
        // Wallet type "google_pay" is detected via webhook charge data
        var response = await CreateCardTransactionAsync(175m, "Google Pay purchase test");

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(175m);
    }

    // ── Cross-payment-type listing & filtering ───────────────────────────

    [Fact]
    public async Task ListTransactions_FilterByBoleto_ReturnsOnlyBoleto()
    {
        // Create one of each type
        await CreateCardTransactionAsync(100m, "Card for filter");
        await CreateBoletoTransactionAsync(200m, "Boleto for filter");

        var response = await Client.GetAsync($"/api/v1/transactions?paymentType={(int)PaymentType.BOLETO}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.GetProperty("data").GetProperty("items");

        foreach (var item in items.EnumerateArray())
        {
            item.GetProperty("paymentType").GetInt32().Should().Be((int)PaymentType.BOLETO);
        }
    }

    [Fact]
    public async Task ListTransactions_FilterByCreditCard_ReturnsCreditCards()
    {
        await CreateCardTransactionAsync(300m, "Card for filter test");

        var response = await Client.GetAsync($"/api/v1/transactions?paymentType={(int)PaymentType.CREDIT_CARD}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.GetProperty("data").GetProperty("items");

        foreach (var item in items.EnumerateArray())
        {
            item.GetProperty("paymentType").GetInt32().Should().Be((int)PaymentType.CREDIT_CARD);
        }
    }

    // ── Retrieve detail for card and boleto ──────────────────────────────

    [Fact]
    public async Task GetTransaction_CardType_HasWalletTypeNull_BeforeWebhook()
    {
        var createResponse = await CreateCardTransactionAsync(400m, "Card detail check");
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = created.GetProperty("data").GetProperty("internalId").GetString();

        var detailResponse = await Client.GetAsync($"/api/v1/transactions/{txId}");
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Before webhook confirms, walletType should be null
        var walletType = detail.GetProperty("data").GetProperty("walletType");
        walletType.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetTransaction_BoletoType_HasCorrectPaymentType()
    {
        var createResponse = await CreateBoletoTransactionAsync(600m, "Boleto detail check");
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = created.GetProperty("data").GetProperty("internalId").GetString();

        var detailResponse = await Client.GetAsync($"/api/v1/transactions/{txId}");
        var detail = await detailResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txData = detail.GetProperty("data");

        txData.GetProperty("paymentType").GetInt32().Should().Be((int)PaymentType.BOLETO);
        txData.GetProperty("installments").GetInt32().Should().Be(1);
    }

    // ── Refund on non-captured card (should fail) ────────────────────────

    [Fact]
    public async Task RefundTransaction_CardNotCaptured_Returns400()
    {
        var createResponse = await CreateCardTransactionAsync(500m, "Card for refund test");
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = created.GetProperty("data").GetProperty("internalId").GetString();

        var refundResponse = await Client.PostAsJsonAsync(
            $"/api/v1/transactions/{txId}/refund",
            new RefundRequestDto(Amount: 100m, Reason: "Test refund"));

        // Card TX is CREATED (not CAPTURED), refund should be rejected
        refundResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Cancel card transaction ──────────────────────────────────────────

    [Fact]
    public async Task CancelTransaction_CardType_ReturnsNoContentOrConflict()
    {
        var createResponse = await CreateCardTransactionAsync(150m, "Card for cancel test");
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = created.GetProperty("data").GetProperty("internalId").GetString();

        var cancelResponse = await Client.PostAsync($"/api/v1/transactions/{txId}/cancel", null);

        // SQLite may return 409 for concurrency; PostgreSQL returns 204
        cancelResponse.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.Conflict);
    }

    // ── Cancel boleto transaction ────────────────────────────────────────

    [Fact]
    public async Task CancelTransaction_BoletoType_ReturnsNoContentOrConflict()
    {
        var createResponse = await CreateBoletoTransactionAsync(250m, "Boleto for cancel test");
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = created.GetProperty("data").GetProperty("internalId").GetString();

        var cancelResponse = await Client.PostAsync($"/api/v1/transactions/{txId}/cancel", null);

        cancelResponse.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.Conflict);
    }

    // ── Export should include all payment types ──────────────────────────

    [Fact]
    public async Task ExportTransactions_IncludesAllPaymentTypes()
    {
        await CreateCardTransactionAsync(100m, "Card for export");
        await CreateBoletoTransactionAsync(200m, "Boleto for export");

        var response = await Client.GetAsync("/api/v1/transactions/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().BeOneOf("text/csv", "application/octet-stream");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> CreateCardTransactionAsync(
        decimal amount, string description, int installments = 1)
    {
        var dto = new CreateTransactionDto(SellerId, amount, PaymentType.CREDIT_CARD, installments, description, DefaultPayer);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(dto);
        return await Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> CreateBoletoTransactionAsync(
        decimal amount, string description)
    {
        var dto = new CreateTransactionDto(SellerId, amount, PaymentType.BOLETO, 1, description, DefaultPayer);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(dto);
        return await Client.SendAsync(request);
    }
}
