using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

public class ApplePayEndpointTests : IntegrationTestBase
{
    private const string BaseUrl = "/api/v1/apple-pay/domains";

    [Fact]
    public async Task RegisterDomain_WithValidRequest_ReturnsCreated()
    {
        var request = new { DomainName = "pay.example.com" };

        var response = await Client.PostAsJsonAsync(BaseUrl, request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = json.GetProperty("data");
        data.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        data.GetProperty("domainName").GetString().Should().Be("pay.example.com");
    }

    [Fact]
    public async Task RegisterDomain_WithoutAuth_ReturnsUnauthorized()
    {
        var client = CreateUnauthenticatedClient();
        var request = new { DomainName = "pay.example.com" };

        var response = await client.PostAsJsonAsync(BaseUrl, request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListDomains_ReturnsOk()
    {
        // Register a domain first so the list is not empty
        await Client.PostAsJsonAsync(BaseUrl, new { DomainName = "shop.example.com" });

        var response = await Client.GetAsync(BaseUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetArrayLength().Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task ListDomains_WithoutAuth_ReturnsUnauthorized()
    {
        var client = CreateUnauthenticatedClient();

        var response = await client.GetAsync(BaseUrl);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteDomain_ReturnsNoContent()
    {
        // Register a domain first to get a valid ID
        var createResponse = await Client.PostAsJsonAsync(BaseUrl, new { DomainName = "delete-me.example.com" });
        var createJson = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var domainId = createJson.GetProperty("data").GetProperty("id").GetString()!;

        var response = await Client.DeleteAsync($"{BaseUrl}/{domainId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
