using System.Net;
using FluentAssertions;
using FellowCore.Integration.Tests.Fixtures;

namespace FellowCore.Integration.Tests;

public class SettlementEndpointTests : IntegrationTestBase
{
    private const string TestMasterKey = "test-master-key-for-integration-tests-minimum-32-chars";

    [Fact]
    public async Task ProcessDailySettlements_WithMasterKey_ReturnsOk()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settlements/process-daily");
        request.Headers.Add("X-Master-Key", TestMasterKey);
        var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProcessDailySettlements_WithApiKeyOnly_Returns401()
    {
        var response = await Client.PostAsync("/api/v1/settlements/process-daily", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProcessDailySettlements_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsync("/api/v1/settlements/process-daily", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
