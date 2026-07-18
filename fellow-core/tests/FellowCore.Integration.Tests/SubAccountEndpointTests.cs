using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FellowCore.Integration.Tests.Fixtures;

namespace FellowCore.Integration.Tests;

public class SubAccountEndpointTests : IntegrationTestBase
{
    // ── Create SubAccount ─────────────────────────────────────────────

    [Fact]
    public async Task CreateSubAccount_ValidData_Returns200()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers/subaccounts", new
        {
            pixKey = "test@test.com",
            name = "Test Sub Account"
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("pixKey").GetString().Should().Be("test@test.com");
    }

    [Fact]
    public async Task CreateSubAccount_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsJsonAsync("/api/v1/sellers/subaccounts", new
        {
            pixKey = "test@test.com",
            name = "Test Sub"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── List SubAccounts ──────────────────────────────────────────────

    [Fact]
    public async Task ListSubAccounts_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/v1/sellers/subaccounts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListSubAccounts_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/sellers/subaccounts");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Get SubAccount ────────────────────────────────────────────────

    [Fact]
    public async Task GetSubAccount_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/v1/sellers/subaccounts/test@test.com");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("pixKey").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetSubAccount_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/sellers/subaccounts/test@test.com");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Delete SubAccount ─────────────────────────────────────────────

    [Fact]
    public async Task DeleteSubAccount_Returns204()
    {
        var response = await Client.DeleteAsync("/api/v1/sellers/subaccounts/delete-test@test.com");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteSubAccount_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.DeleteAsync("/api/v1/sellers/subaccounts/test@test.com");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Credit SubAccount ─────────────────────────────────────────────

    [Fact]
    public async Task CreditSubAccount_ValidData_Returns200()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers/subaccounts/test@test.com/credit", new
        {
            amount = 100.00m,
            description = "Test credit"
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreditSubAccount_InvalidAmount_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers/subaccounts/test@test.com/credit", new
        {
            amount = -10m
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Debit SubAccount ──────────────────────────────────────────────

    [Fact]
    public async Task DebitSubAccount_ValidData_Returns200()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers/subaccounts/test@test.com/debit", new
        {
            amount = 50.00m,
            description = "Test debit"
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DebitSubAccount_InvalidAmount_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers/subaccounts/test@test.com/debit", new
        {
            amount = -5m
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Transfer Between SubAccounts ──────────────────────────────────

    [Fact]
    public async Task TransferBetweenSubAccounts_ValidData_Returns200()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers/subaccounts/transfer", new
        {
            amount = 25.00m,
            fromPixKey = "sender@test.com",
            toPixKey = "receiver@test.com"
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TransferBetweenSubAccounts_InvalidAmount_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers/subaccounts/transfer", new
        {
            amount = 0m,
            fromPixKey = "sender@test.com",
            toPixKey = "receiver@test.com"
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TransferBetweenSubAccounts_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.PostAsJsonAsync("/api/v1/sellers/subaccounts/transfer", new
        {
            amount = 25m,
            fromPixKey = "sender@test.com",
            toPixKey = "receiver@test.com"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Withdraw From SubAccount ──────────────────────────────────────

    [Fact]
    public async Task WithdrawFromSubAccount_ValidData_Returns200()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers/subaccounts/test@test.com/withdraw", new
        {
            amount = 75.00m
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WithdrawFromSubAccount_InvalidAmount_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/sellers/subaccounts/test@test.com/withdraw", new
        {
            amount = -1m
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── SubAccount Statement ──────────────────────────────────────────

    [Fact]
    public async Task GetSubAccountStatement_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/v1/sellers/subaccounts/test@test.com/statement");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSubAccountStatement_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/sellers/subaccounts/test@test.com/statement");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
