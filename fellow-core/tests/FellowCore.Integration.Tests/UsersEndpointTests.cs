using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FellowCore.Integration.Tests.Fixtures;

namespace FellowCore.Integration.Tests;

// UsersController uses [Authorize] (JWT Bearer), not [ApiKeyAuth].
// The authenticated Client carries X-Api-Key only, so all requests through it
// are treated as unauthenticated by this controller.
// Tests that require an authenticated user obtain a JWT via the auth endpoint.
public class UsersEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task CreateUser_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsJsonAsync("/api/v1/users", new
        {
            name = "New User",
            email = "newuser@test.com",
            password = "StrongP@ss123!",
            role = 0
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListUsers_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/users");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListUsers_WithJwtAuth_ReturnsOk()
    {
        await TestDataHelper.SeedUserAsync(Factory.Services, TenantId);
        var accessToken = await LoginAndGetAccessTokenAsync();

        var jwtClient = CreateUnauthenticatedClient();
        jwtClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await jwtClient.GetAsync("/api/v1/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        body.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task DeleteUser_NotFound_Returns404()
    {
        await TestDataHelper.SeedUserAsync(Factory.Services, TenantId);
        var accessToken = await LoginAndGetAccessTokenAsync();

        var jwtClient = CreateUnauthenticatedClient();
        jwtClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await jwtClient.DeleteAsync($"/api/v1/users/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── helpers ──

    private async Task<string> LoginAndGetAccessTokenAsync()
    {
        var loginResponse = await Client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = TestDataHelper.TestUserEmail,
            password = TestDataHelper.TestUserPassword
        });
        var body = await DeserializeAsync(loginResponse);
        return body.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> DeserializeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
    }
}
