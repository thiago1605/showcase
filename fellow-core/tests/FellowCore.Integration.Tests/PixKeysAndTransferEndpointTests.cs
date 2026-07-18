using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FellowCore.Integration.Tests.Fixtures;

namespace FellowCore.Integration.Tests;

public class PixKeysAndTransferEndpointTests : IntegrationTestBase
{
    // ── List Pix Keys ─────────────────────────────────────────────────

    [Fact]
    public async Task ListPixKeys_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/v1/pix/keys");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListPixKeys_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/pix/keys");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Create Pix Key ────────────────────────────────────────────────

    [Fact]
    public async Task CreatePixKey_ValidData_Returns201()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/pix/keys", new
        {
            key = "newkey@test.com",
            type = "EMAIL"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreatePixKey_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsJsonAsync("/api/v1/pix/keys", new
        {
            key = "key@test.com",
            type = "EMAIL"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Delete Pix Key ────────────────────────────────────────────────

    [Fact]
    public async Task DeletePixKey_Returns204()
    {
        var response = await Client.DeleteAsync("/api/v1/pix/keys/delete-test@test.com");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeletePixKey_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.DeleteAsync("/api/v1/pix/keys/test@test.com");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Set Default Pix Key ───────────────────────────────────────────

    [Fact]
    public async Task SetDefaultPixKey_ReturnsOk()
    {
        var response = await Client.PostAsync("/api/v1/pix/keys/test@test.com/default", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetDefaultPixKey_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsync("/api/v1/pix/keys/test@test.com/default", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Create Pix Transfer ───────────────────────────────────────────

    [Fact]
    public async Task CreatePixTransfer_ValidData_ReturnsOk()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/pix/transfers", new
        {
            amount = 150.00m,
            fromPixKey = "from@test.com",
            toPixKey = "to@test.com"
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreatePixTransfer_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsJsonAsync("/api/v1/pix/transfers", new
        {
            amount = 150m,
            fromPixKey = "from@test.com",
            toPixKey = "to@test.com"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Approve Pix Payment ───────────────────────────────────────────

    [Fact]
    public async Task ApprovePixPayment_ReturnsOk()
    {
        var response = await Client.PostAsync("/api/v1/pix/payments/test-correlation-id/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ApprovePixPayment_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsync("/api/v1/pix/payments/test-correlation-id/approve", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
