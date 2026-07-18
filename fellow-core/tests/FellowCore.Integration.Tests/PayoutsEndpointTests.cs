using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Modules.Payouts.DTOs;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

public class PayoutsEndpointTests : IntegrationTestBase
{
    [Fact]
    public async Task CreatePayout_WithValidData_Returns201()
    {
        var dto = new CreatePayoutDto(SellerId, Amount: 100m, Fee: 1m);

        var response = await Client.PostAsJsonAsync("/api/v1/payouts", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(100m);
    }

    [Fact]
    public async Task CreatePayout_WithZeroAmount_Returns400()
    {
        var dto = new CreatePayoutDto(SellerId, Amount: 0m);

        var response = await Client.PostAsJsonAsync("/api/v1/payouts", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreatePayout_WithInvalidSeller_Returns404()
    {
        var dto = new CreatePayoutDto(Guid.NewGuid(), Amount: 100m);

        var response = await Client.PostAsJsonAsync("/api/v1/payouts", dto);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPayoutById_AfterCreate_Returns200()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/payouts",
            new CreatePayoutDto(SellerId, Amount: 200m, Fee: 2m));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var payoutId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.GetAsync($"/api/v1/payouts/{payoutId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(200m);
    }

    [Fact]
    public async Task GetPayoutById_NotFound_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/payouts/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListPayouts_ReturnsPaginatedResult()
    {
        await Client.PostAsJsonAsync("/api/v1/payouts",
            new CreatePayoutDto(SellerId, Amount: 50m));

        var response = await Client.GetAsync("/api/v1/payouts?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("items").GetArrayLength().Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task ListPayouts_WithSellerFilter_ReturnsFiltered()
    {
        await Client.PostAsJsonAsync("/api/v1/payouts",
            new CreatePayoutDto(SellerId, Amount: 75m));

        var response = await Client.GetAsync($"/api/v1/payouts?sellerId={SellerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("items").GetArrayLength().Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task CreatePayout_WithoutAuth_Returns401()
    {
        var client = CreateUnauthenticatedClient();
        var dto = new CreatePayoutDto(SellerId, Amount: 100m);

        var response = await client.PostAsJsonAsync("/api/v1/payouts", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
