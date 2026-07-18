using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Modules.Subscriptions.DTOs;
using FellowCore.Domain.Enums;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

public class SubscriptionsEndpointTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateSubscription_WithValidData_Returns201()
    {
        var dto = new CreateSubscriptionDto(SellerId, 99.90m, "Plano Mensal", BillingInterval.MONTHLY);

        var response = await Client.PostAsJsonAsync("/api/v1/subscriptions", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(99.90m);
    }

    [Fact]
    public async Task CreateSubscription_WithInvalidSeller_Returns404()
    {
        var dto = new CreateSubscriptionDto(Guid.NewGuid(), 100m, "Plano", BillingInterval.MONTHLY);

        var response = await Client.PostAsJsonAsync("/api/v1/subscriptions", dto);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateSubscription_WithZeroAmount_Returns400()
    {
        var dto = new CreateSubscriptionDto(SellerId, 0m, "Plano", BillingInterval.MONTHLY);

        var response = await Client.PostAsJsonAsync("/api/v1/subscriptions", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetSubscriptionById_AfterCreate_ReturnsSubscription()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/subscriptions",
            new CreateSubscriptionDto(SellerId, 49.90m, "Plano Básico", BillingInterval.WEEKLY));
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var subId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.GetAsync($"/api/v1/subscriptions/{subId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(49.90m);
        json.GetProperty("data").GetProperty("description").GetString().Should().Be("Plano Básico");
    }

    [Fact]
    public async Task ListSubscriptions_ReturnsPaginatedResult()
    {
        await Client.PostAsJsonAsync("/api/v1/subscriptions",
            new CreateSubscriptionDto(SellerId, 100m, "Plano A", BillingInterval.MONTHLY));
        await Client.PostAsJsonAsync("/api/v1/subscriptions",
            new CreateSubscriptionDto(SellerId, 200m, "Plano B", BillingInterval.YEARLY));

        var response = await Client.GetAsync("/api/v1/subscriptions?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("items").GetArrayLength().Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task CancelSubscription_ReturnsUpdatedSubscription()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/subscriptions",
            new CreateSubscriptionDto(SellerId, 150m, "Plano Cancel", BillingInterval.MONTHLY));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var subId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.PostAsync($"/api/v1/subscriptions/{subId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PauseAndResume_WorksCorrectly()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/subscriptions",
            new CreateSubscriptionDto(SellerId, 80m, "Plano Pausável", BillingInterval.MONTHLY));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var subId = created.GetProperty("data").GetProperty("id").GetString();

        // Pause
        var pauseResponse = await Client.PostAsync($"/api/v1/subscriptions/{subId}/pause", null);
        pauseResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Resume
        var resumeResponse = await Client.PostAsync($"/api/v1/subscriptions/{subId}/resume", null);
        resumeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CancelSubscription_NotFound_Returns404()
    {
        var response = await Client.PostAsync($"/api/v1/subscriptions/{Guid.NewGuid()}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
