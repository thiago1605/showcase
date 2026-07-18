using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Modules.Sellers.DTOs;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Infrastructure.Database;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FellowCore.Integration.Tests;

public class SellersEndpointTests : IntegrationTestBase
{
    private static readonly SellerAddressDto DefaultAddress = new(
        Street: "Rua Teste",
        Number: "123",
        Complement: "Sala 1",
        Neighborhood: "Centro",
        City: "Salvador",
        State: "BA",
        ZipCode: "40000000"
    );

    [Fact]
    public async Task CreateSeller_WithValidData_Returns201()
    {
        var dto = BuildCreateSellerDto();

        var response = await Client.PostAsJsonAsync("/api/v1/sellers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("legalName").GetString().Should().Be("Empresa Teste LTDA");
    }

    [Fact]
    public async Task CreateSeller_WithMissingLegalName_Returns400()
    {
        var dto = BuildCreateSellerDto(legalName: "");

        var response = await Client.PostAsJsonAsync("/api/v1/sellers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSeller_WithMissingDocument_Returns400()
    {
        var dto = BuildCreateSellerDto(document: "");

        var response = await Client.PostAsJsonAsync("/api/v1/sellers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSeller_WithMissingEmail_Returns400()
    {
        var dto = BuildCreateSellerDto(email: "");

        var response = await Client.PostAsJsonAsync("/api/v1/sellers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetSellerById_ExistingSeller_Returns200()
    {
        var response = await Client.GetAsync($"/api/v1/sellers/{SellerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("id").GetString().Should().Be(SellerId.ToString());
    }

    [Fact]
    public async Task GetSellerById_NotFound_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/sellers/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListSellers_ReturnsPaginatedResult()
    {
        var response = await Client.GetAsync("/api/v1/sellers?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("items").GetArrayLength().Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task CreateSeller_WithoutAuth_Returns401()
    {
        var client = CreateUnauthenticatedClient();
        var dto = BuildCreateSellerDto();

        var response = await client.PostAsJsonAsync("/api/v1/sellers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetBalance_ExistingSeller_ReturnsOk()
    {
        var response = await Client.GetAsync($"/api/v1/sellers/{SellerId}/balance");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetBalance_NotFound_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/sellers/{Guid.NewGuid()}/balance");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetStatement_ExistingSeller_ReturnsOk()
    {
        var response = await Client.GetAsync($"/api/v1/sellers/{SellerId}/statement?start=2026-01-01&end=2026-12-31");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Withdraw_ValidAmount_Returns200or201()
    {
        var openPixSellerId = await SeedOpenPixSellerAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/sellers/{openPixSellerId}/withdraw",
            new SellerWithdrawRequestDto(Amount: 100m));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(100m);
    }

    [Fact]
    public async Task Withdraw_NotFound_Returns404()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/sellers/{Guid.NewGuid()}/withdraw",
            new SellerWithdrawRequestDto(Amount: 100m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Guid> SeedOpenPixSellerAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var seller = Seller.Create(
            tenantId: TenantId,
            legalName: "OpenPix Seller LTDA",
            document: "99988877000166",
            email: "openpix-seller@test.com",
            webhookSecret: "openpix-webhook-secret-32chars!!",
            preferredProvider: PaymentProvider.OPENPIX,
            externalAccountId: "openpix-acc-123",
            encryptedAccessToken: "enc:fake-openpix-appid",
            tradeName: "OpenPix Seller",
            pixKey: "99988877000166");

        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        return seller.Id;
    }

    private static CreateSellerDto BuildCreateSellerDto(
        string legalName = "Empresa Teste LTDA",
        string document = "98765432000188",
        string email = "empresa@test.com")
    {
        return new CreateSellerDto(
            LegalName: legalName,
            TradeName: "Empresa Teste",
            Document: document,
            Email: email,
            IncomeValue: 10000m,
            BirthDate: "1990-01-15",
            MobilePhone: "71999998888",
            Address: DefaultAddress,
            FeeDebit: null,
            FeeCreditCash: null,
            FeeCreditInstallment: null,
            FeePixIn: null,
            PayoutFixedFee: null,
            PayoutPercentFee: null,
            BusinessDescription: "Loja de testes",
            BusinessProduct: "Produtos digitais",
            BusinessLifetime: "2 anos",
            BusinessGoal: "Escalar vendas",
            Documents: null
        );
    }
}
