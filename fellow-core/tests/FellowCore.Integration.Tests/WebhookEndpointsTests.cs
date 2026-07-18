using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

public class WebhookEndpointsTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateEndpoint_WithValidData_Returns201()
    {
        var dto = new CreateWebhookEndpointDto(
            Url: "https://example.com/webhooks",
            Secret: "my-secret-key-32chars-at-least!!");

        var response = await Client.PostAsJsonAsync("/api/v1/webhook-endpoints", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("url").GetString().Should().Be("https://example.com/webhooks");
    }

    [Fact]
    public async Task CreateEndpoint_WithEvents_Returns201()
    {
        var dto = new CreateWebhookEndpointDto(
            Url: "https://example.com/hooks",
            Secret: "another-secret-key-32chars-ok!!",
            Events: ["transaction.captured", "transaction.refunded"]);

        var response = await Client.PostAsJsonAsync("/api/v1/webhook-endpoints", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("events").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task CreateEndpoint_WithMissingUrl_Returns400()
    {
        var dto = new CreateWebhookEndpointDto(Url: "", Secret: "secret-key");

        var response = await Client.PostAsJsonAsync("/api/v1/webhook-endpoints", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEndpoint_WithMissingSecret_Returns400()
    {
        var dto = new CreateWebhookEndpointDto(Url: "https://example.com/hooks", Secret: "");

        var response = await Client.PostAsJsonAsync("/api/v1/webhook-endpoints", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListEndpoints_ReturnsPaginatedResult()
    {
        await Client.PostAsJsonAsync("/api/v1/webhook-endpoints",
            new CreateWebhookEndpointDto("https://hook1.test/cb", "secret-1-32chars-at-least-ok!!"));
        await Client.PostAsJsonAsync("/api/v1/webhook-endpoints",
            new CreateWebhookEndpointDto("https://hook2.test/cb", "secret-2-32chars-at-least-ok!!"));

        var response = await Client.GetAsync("/api/v1/webhook-endpoints?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("items").GetArrayLength().Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task DeleteEndpoint_AfterCreate_Returns204()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/webhook-endpoints",
            new CreateWebhookEndpointDto("https://to-delete.test/cb", "secret-delete-32chars-ok!!!!!!"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var endpointId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.DeleteAsync($"/api/v1/webhook-endpoints/{endpointId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteEndpoint_NotFound_Returns404()
    {
        var response = await Client.DeleteAsync($"/api/v1/webhook-endpoints/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateEndpoint_WithoutAuth_Returns401()
    {
        var client = CreateUnauthenticatedClient();
        var dto = new CreateWebhookEndpointDto(
            "https://example.com/hooks",
            "valid-secret-32chars-at-least!!");

        var response = await client.PostAsJsonAsync("/api/v1/webhook-endpoints", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDeliveries_AfterCreate_ReturnsEmptyPage()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/webhook-endpoints",
            new CreateWebhookEndpointDto("https://deliveries.test/cb", "secret-deliveries-32chars-ok!!"));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var endpointId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.GetAsync($"/api/v1/webhook-endpoints/{endpointId}/deliveries");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetDeliveries_NonExistentEndpoint_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/webhook-endpoints/{Guid.NewGuid()}/deliveries");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RetryDelivery_NonExistentEndpoint_Returns404()
    {
        var response = await Client.PostAsync(
            $"/api/v1/webhook-endpoints/{Guid.NewGuid()}/deliveries/{Guid.NewGuid()}/retry", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RetryDelivery_NonExistentDelivery_Returns404()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/webhook-endpoints",
            new CreateWebhookEndpointDto("https://retry.test/cb", "secret-retry-32chars-ok-here!!"));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var endpointId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.PostAsync(
            $"/api/v1/webhook-endpoints/{endpointId}/deliveries/{Guid.NewGuid()}/retry", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
