using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

public class TenantsEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── GET /api/v1/tenants/me ──────────────────────────────────────────────

    [Fact]
    public async Task GetProfile_Authenticated_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/v1/tenants/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        var data = body.GetProperty("data");
        data.GetProperty("id").GetString().Should().Be(TenantId.ToString());
        data.GetProperty("name").GetString().Should().Be("Test Tenant");
        data.GetProperty("slug").GetString().Should().Be("test-tenant");
        data.GetProperty("maskedApiKey").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetProfile_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/tenants/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── POST /api/v1/tenants ────────────────────────────────────────────────

    private const string TestMasterKey = "test-master-key-for-integration-tests-minimum-32-chars";

    private HttpRequestMessage CreateTenantRequest(object dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenants");
        request.Content = JsonContent.Create(dto);
        request.Headers.Add("X-Master-Key", TestMasterKey);
        return request;
    }

    [Fact]
    public async Task CreateTenant_ValidData_Returns201()
    {
        var dto = new { name = "New Tenant", slug = "new-tenant" };

        var response = await Client.SendAsync(CreateTenantRequest(dto));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await DeserializeAsync(response);
        var data = body.GetProperty("data");
        var tenant = data.GetProperty("tenant");
        tenant.GetProperty("name").GetString().Should().Be("New Tenant");
        tenant.GetProperty("slug").GetString().Should().Be("new-tenant");
        data.GetProperty("apiSecret").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateTenant_MissingName_Returns400()
    {
        var dto = new { name = "", slug = "missing-name-tenant" };

        var response = await Client.SendAsync(CreateTenantRequest(dto));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTenant_InvalidSlug_Returns400()
    {
        // Slug with spaces is not allowed — must match ^[a-z0-9]+(?:-[a-z0-9]+)*$
        var dto = new { name = "Bad Slug Tenant", slug = "bad slug with spaces" };

        var response = await Client.SendAsync(CreateTenantRequest(dto));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTenant_WithoutMasterKey_Returns401()
    {
        var dto = new { name = "Unauthorized Tenant", slug = "unauth-tenant" };

        var response = await Client.PostAsJsonAsync("/api/v1/tenants", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PATCH /api/v1/tenants/me/providers ─────────────────────────────────

    [Fact]
    public async Task UpdateProviders_ValidData_Returns200()
    {
        // PaymentProvider enum: STRIPE = 0, OPENPIX = 1, SANDBOX = 2
        var dto = new { activePixProvider = 1 };

        var response = await Client.PatchAsJsonAsync("/api/v1/tenants/me/providers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        body.GetProperty("data").GetProperty("message").GetString()
            .Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UpdateProviders_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var dto = new { activePixProvider = 1 };

        var response = await unauthClient.PatchAsJsonAsync("/api/v1/tenants/me/providers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /api/v1/tenants/me/provider-webhooks ───────────────────────────

    [Fact]
    public async Task ListProviderWebhooks_Authenticated_ReturnsHttpResponse()
    {
        // IOpenPixApiClient is not faked in the integration test environment.
        // The real HTTP client will attempt to reach the OpenPix API, which is
        // unavailable in tests and will result in a non-404 error response.
        // This test verifies the route exists and the auth guard is in place —
        // not that the downstream call succeeds.
        var response = await Client.GetAsync("/api/v1/tenants/me/provider-webhooks");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListProviderWebhooks_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/tenants/me/provider-webhooks");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<JsonElement> DeserializeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
    }
}
