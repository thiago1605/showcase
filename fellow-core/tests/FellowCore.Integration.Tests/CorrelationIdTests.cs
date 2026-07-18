using System.Net;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

public class CorrelationIdTests : IntegrationTestBase
{
    [Fact]
    public async Task Response_ContainsCorrelationIdHeader()
    {
        var response = await Client.GetAsync("/api/v1/sellers");

        response.Headers.Contains("X-Correlation-Id").Should().BeTrue();
        response.Headers.GetValues("X-Correlation-Id").First().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Response_EchoesProvidedCorrelationId()
    {
        var correlationId = "test-correlation-12345";
        Client.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);

        var response = await Client.GetAsync("/api/v1/sellers");

        response.Headers.GetValues("X-Correlation-Id").First().Should().Be(correlationId);
    }

    [Fact]
    public async Task Response_GeneratesCorrelationId_WhenNotProvided()
    {
        var response = await Client.GetAsync("/health");

        response.Headers.Contains("X-Correlation-Id").Should().BeTrue();
        var id = response.Headers.GetValues("X-Correlation-Id").First();
        id.Should().HaveLength(32); // Guid without dashes
    }
}
