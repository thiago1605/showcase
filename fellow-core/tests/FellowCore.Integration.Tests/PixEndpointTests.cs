using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FellowCore.Integration.Tests.Fixtures;

namespace FellowCore.Integration.Tests;

public class PixEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── Validate Pix Key ────────────────────────────────────────────

    [Fact]
    public async Task ValidatePixKey_ValidKey_ReturnsOk()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/pix/validate-key", new { pixKey = "12345678901" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeAsync(response);
        body.GetProperty("data").GetProperty("key").GetString().Should().Be("12345678901");
    }

    [Fact]
    public async Task ValidatePixKey_EmptyKey_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/pix/validate-key", new { pixKey = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ValidatePixKey_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsJsonAsync("/api/v1/pix/validate-key", new { pixKey = "12345678901" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Pix Payments ────────────────────────────────────────────────

    [Fact]
    public async Task CreatePixPayment_ValidData_Returns201()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/pix/payments", new
        {
            destinationPixKey = "98765432100",
            amount = 50.00m,
            description = "Test Pix Payment"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await DeserializeAsync(response);
        var data = body.GetProperty("data");
        data.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
        data.GetProperty("status").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreatePixPayment_InvalidAmount_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/pix/payments", new
        {
            destinationPixKey = "98765432100",
            amount = -10m
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreatePixPayment_MissingPixKey_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/pix/payments", new
        {
            destinationPixKey = "",
            amount = 50m
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListPixPayments_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/v1/pix/payments?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPixPayment_NotFound_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/pix/payments/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPixPayment_AfterCreate_ReturnsOk()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/pix/payments", new
        {
            destinationPixKey = "11122233344",
            amount = 25.00m
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await DeserializeAsync(createResponse);
        var id = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.GetAsync($"/api/v1/pix/payments/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PixPayments_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/pix/payments");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Static QR Codes ─────────────────────────────────────────────

    [Fact]
    public async Task CreateStaticQr_ValidData_Returns201()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/pix/qr-codes", new
        {
            name = "Test QR Code",
            amount = 100m,
            description = "Test static QR"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await DeserializeAsync(response);
        body.GetProperty("data").GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateStaticQr_MissingName_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/pix/qr-codes", new
        {
            name = "",
            amount = 100m
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListStaticQr_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/v1/pix/qr-codes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteStaticQr_Returns204()
    {
        var response = await Client.DeleteAsync("/api/v1/pix/qr-codes/test-qr-id");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static async Task<JsonElement> DeserializeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
    }
}
