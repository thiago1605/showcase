using System.Net;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

public class AuthenticationTests : IntegrationTestBase
{
    [Fact]
    public async Task Request_WithoutApiKey_Returns401()
    {
        var client = CreateUnauthenticatedClient();

        var response = await client.GetAsync("/api/v1/sellers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_WithInvalidApiKey_Returns401()
    {
        var client = CreateUnauthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "invalid-key");

        var response = await client.GetAsync("/api/v1/sellers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_WithValidApiKey_Returns200()
    {
        var response = await Client.GetAsync("/api/v1/sellers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthCheck_NoAuth_ReturnsWithoutAuth()
    {
        var client = CreateUnauthenticatedClient();

        var response = await client.GetAsync("/health");

        // Health check endpoint is accessible without auth.
        // In test environment (no real Postgres/Redis), it may return 503 (Unhealthy).
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }
}
