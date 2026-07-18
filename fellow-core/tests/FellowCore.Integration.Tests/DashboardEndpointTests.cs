using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FellowCore.Integration.Tests.Fixtures;

namespace FellowCore.Integration.Tests;

public class DashboardEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task GetDashboard_Authenticated_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/v1/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        var data = body.GetProperty("data");
        data.GetProperty("totalVolume").GetDecimal().Should().BeGreaterThanOrEqualTo(0);
        data.GetProperty("transactionCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetDashboard_WithDateFilter_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/v1/dashboard?from=2026-01-01&to=2026-12-31");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        var data = body.GetProperty("data");
        data.GetProperty("totalVolume").GetDecimal().Should().BeGreaterThanOrEqualTo(0);
        data.GetProperty("transactionCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetDashboard_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<JsonElement> DeserializeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
    }
}
