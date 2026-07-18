using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FellowCore.Integration.Tests.Fixtures;

namespace FellowCore.Integration.Tests;

public class ScheduledReportsEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task CreateReport_ValidData_Returns201()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/scheduled-reports", new
        {
            reportType = 0,
            format = 0,
            frequency = 0,
            recipients = "test@test.com"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await DeserializeAsync(response);
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        var data = body.GetProperty("data");
        data.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("recipients").GetString().Should().Be("test@test.com");
        data.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ListReports_ReturnsOk()
    {
        // Seed one report so the list is non-empty
        await Client.PostAsJsonAsync("/api/v1/scheduled-reports", new
        {
            reportType = 0,
            format = 0,
            frequency = 0,
            recipients = "list@test.com"
        });

        var response = await Client.GetAsync("/api/v1/scheduled-reports");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        body.GetProperty("success").GetBoolean().Should().BeTrue();
        body.GetProperty("data").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetReport_NotFound_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/scheduled-reports/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetReport_AfterCreate_ReturnsReport()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/scheduled-reports", new
        {
            reportType = 0,
            format = 0,
            frequency = 0,
            recipients = "get@test.com"
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await DeserializeAsync(createResponse);
        var reportId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.GetAsync($"/api/v1/scheduled-reports/{reportId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        body.GetProperty("data").GetProperty("recipients").GetString().Should().Be("get@test.com");
    }

    [Fact]
    public async Task DisableReport_AfterCreate_Returns204()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/scheduled-reports", new
        {
            reportType = 0,
            format = 0,
            frequency = 0,
            recipients = "disable@test.com"
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await DeserializeAsync(createResponse);
        var reportId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.PostAsync($"/api/v1/scheduled-reports/{reportId}/disable", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task EnableReport_AfterDisable_Returns204()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/scheduled-reports", new
        {
            reportType = 0,
            format = 0,
            frequency = 0,
            recipients = "enable@test.com"
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await DeserializeAsync(createResponse);
        var reportId = created.GetProperty("data").GetProperty("id").GetString();

        await Client.PostAsync($"/api/v1/scheduled-reports/{reportId}/disable", null);
        var response = await Client.PostAsync($"/api/v1/scheduled-reports/{reportId}/enable", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CreateReport_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsJsonAsync("/api/v1/scheduled-reports", new
        {
            reportType = 0,
            format = 0,
            frequency = 0,
            recipients = "unauth@test.com"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<JsonElement> DeserializeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
    }
}
