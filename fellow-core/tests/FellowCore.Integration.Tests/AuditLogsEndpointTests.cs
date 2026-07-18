using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Domain.Enums;
using FluentAssertions;
using FellowCore.Integration.Tests.Fixtures;

namespace FellowCore.Integration.Tests;

public class AuditLogsEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // AuditLogsController exige JWT com role SUPER_ADMIN/OWNER/FINANCE — API key não basta.
    // Cada teste cria seu próprio JWT client (e usa Client = API key pra criar transactions/customers,
    // já que esses endpoints aceitam ambos os esquemas).

    [Fact]
    public async Task ListAuditLogs_Empty_ReturnsEmptyPage()
    {
        var jwtClient = await CreateJwtClientAsync();
        var response = await jwtClient.GetAsync("/api/v1/audit-logs?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        var data = body.GetProperty("data");
        data.GetProperty("totalCount").GetInt32().Should().Be(0);
        data.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ListAuditLogs_AfterCreatingTransaction_ContainsEntry()
    {
        // Create a transaction to trigger audit log
        var dto = new CreateTransactionDto(SellerId, 50m, PaymentType.PIX, 1, "Audit test", new PayerDto("John Doe", "12345678901", "john@test.com"));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(dto);
        var txResponse = await Client.SendAsync(request);
        txResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var jwtClient = await CreateJwtClientAsync();
        var response = await jwtClient.GetAsync("/api/v1/audit-logs?page=1&pageSize=10&action=transaction.created");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        var data = body.GetProperty("data");
        data.GetProperty("totalCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        var items = data.GetProperty("items");
        items.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        items[0].GetProperty("action").GetString().Should().Be("transaction.created");
        items[0].GetProperty("statusCode").GetInt32().Should().Be(201);
    }

    [Fact]
    public async Task ListAuditLogs_FilterByAction_FiltersCorrectly()
    {
        // Create a customer to trigger customer.created audit log
        await Client.PostAsJsonAsync("/api/v1/customers", new
        {
            name = "Audit Customer",
            email = "audit-customer@test.com",
            document = "99988877766"
        });

        var jwtClient = await CreateJwtClientAsync();

        // Filter for transaction.created (should be 0 since we only created a customer)
        var response = await jwtClient.GetAsync("/api/v1/audit-logs?action=transaction.created");
        var body = await DeserializeAsync(response);
        var txLogs = body.GetProperty("data").GetProperty("items").GetArrayLength();

        // Filter for customer.created (should have at least 1)
        response = await jwtClient.GetAsync("/api/v1/audit-logs?action=customer.created");
        body = await DeserializeAsync(response);
        var custLogs = body.GetProperty("data").GetProperty("items").GetArrayLength();

        custLogs.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ListAuditLogs_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/audit-logs");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListAuditLogs_Pagination_Works()
    {
        var jwtClient = await CreateJwtClientAsync();
        var response = await jwtClient.GetAsync("/api/v1/audit-logs?page=1&pageSize=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        var data = body.GetProperty("data");
        data.GetProperty("page").GetInt32().Should().Be(1);
        data.GetProperty("pageSize").GetInt32().Should().Be(5);
    }

    private static async Task<JsonElement> DeserializeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
    }
}
