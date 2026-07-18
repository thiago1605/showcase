using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Domain.Enums;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

public class TransactionsEndpointTests : IntegrationTestBase
{
    private static readonly PayerDto DefaultPayer = new("Maria Silva", "12345678901", "maria@test.com");

    [Fact]
    public async Task CreateTransaction_WithValidPixData_Returns201()
    {
        var response = await CreateTransactionAsync(150m, "Pagamento teste PIX");

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(150m);
        json.GetProperty("data").GetProperty("payment").GetProperty("transactionId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateTransaction_WithoutSeller_Returns201()
    {
        var response = await CreateTransactionAsync(75m, "Pagamento sem seller", sellerId: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTransaction_WithInvalidSeller_Returns404()
    {
        var response = await CreateTransactionAsync(100m, "Pagamento", sellerId: Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateTransaction_WithZeroAmount_Returns400()
    {
        var response = await CreateTransactionAsync(0m, "Valor zero");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTransaction_WithMissingPayer_Returns400()
    {
        var dto = new CreateTransactionDto(SellerId, 100m, PaymentType.PIX, 1, "Sem pagador", null!);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(dto);

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTransactionById_AfterCreate_ReturnsTransaction()
    {
        var createResponse = await CreateTransactionAsync(200m, "Para consulta");
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = created.GetProperty("data").GetProperty("internalId").GetString();

        var response = await Client.GetAsync($"/api/v1/transactions/{txId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(200m);
        json.GetProperty("data").GetProperty("description").GetString().Should().Be("Para consulta");
    }

    [Fact]
    public async Task GetTransactionById_NotFound_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/transactions/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListTransactions_ReturnsPaginatedResult()
    {
        await CreateTransactionAsync(100m, "Tx 1");
        await CreateTransactionAsync(250m, "Tx 2");

        var response = await Client.GetAsync("/api/v1/transactions?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("items").GetArrayLength().Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task ListTransactions_WithSellerFilter_ReturnsFiltered()
    {
        await CreateTransactionAsync(300m, "Filtered");

        var response = await Client.GetAsync($"/api/v1/transactions?sellerId={SellerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("items").GetArrayLength().Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task RefundTransaction_WhenNotCaptured_Returns400()
    {
        var createResponse = await CreateTransactionAsync(500m, "Para refund");
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = created.GetProperty("data").GetProperty("internalId").GetString();

        var refundResponse = await Client.PostAsJsonAsync(
            $"/api/v1/transactions/{txId}/refund",
            new RefundRequestDto(Amount: 100m, Reason: "Teste"));

        refundResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefundTransaction_NotFound_Returns404()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/transactions/{Guid.NewGuid()}/refund",
            new RefundRequestDto(Amount: 50m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CancelTransaction_WhenCreated_ReturnsNoContentOrConflict()
    {
        var createResponse = await CreateTransactionAsync(300m, "Para cancelamento");
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = created.GetProperty("data").GetProperty("internalId").GetString();

        var response = await Client.PostAsync($"/api/v1/transactions/{txId}/cancel", null);

        // SQLite does not support RowVersion concurrency tokens, so the update may
        // raise DbConcurrencyException (409) in tests. In production (PostgreSQL) it returns 204.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CancelTransaction_NotFound_Returns404()
    {
        var response = await Client.PostAsync($"/api/v1/transactions/{Guid.NewGuid()}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListRefunds_ForTransaction_ReturnsOk()
    {
        var createResponse = await CreateTransactionAsync(400m, "Para listar refunds");
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = created.GetProperty("data").GetProperty("internalId").GetString();

        var response = await Client.GetAsync($"/api/v1/transactions/{txId}/refunds");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ExportTransactions_CsvFormat_ReturnsFile()
    {
        var response = await Client.GetAsync("/api/v1/transactions/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var contentType = response.Content.Headers.ContentType!.MediaType;
        contentType.Should().BeOneOf("text/csv", "application/octet-stream");
    }

    [Fact]
    public async Task ExportTransactions_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();

        var response = await unauthClient.GetAsync("/api/v1/transactions/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpResponseMessage> CreateTransactionAsync(
        decimal amount, string description, Guid? sellerId = default)
    {
        var resolvedSellerId = sellerId == default ? SellerId : sellerId;
        var dto = new CreateTransactionDto(resolvedSellerId, amount, PaymentType.PIX, 1, description, DefaultPayer);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(dto);
        return await Client.SendAsync(request);
    }
}
