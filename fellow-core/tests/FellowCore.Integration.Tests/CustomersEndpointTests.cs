using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Modules.Customers.DTOs;
using FellowCore.Domain.Enums;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

public class CustomersEndpointTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateCustomer_WithValidData_Returns201()
    {
        var dto = new CreateCustomerDto("Carlos Silva", $"carlos-{Guid.NewGuid():N}@test.com", "98765432100");

        var response = await Client.PostAsJsonAsync("/api/v1/customers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("name").GetString().Should().Be("Carlos Silva");
    }

    [Fact]
    public async Task CreateCustomer_WithMissingName_Returns400()
    {
        var dto = new CreateCustomerDto("", "test@test.com");

        var response = await Client.PostAsJsonAsync("/api/v1/customers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateCustomer_WithMissingEmail_Returns400()
    {
        var dto = new CreateCustomerDto("Nome Teste", "");

        var response = await Client.PostAsJsonAsync("/api/v1/customers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetCustomerById_AfterCreate_Returns200()
    {
        var uniqueEmail = $"ana-{Guid.NewGuid():N}@test.com";
        var createResponse = await Client.PostAsJsonAsync("/api/v1/customers",
            new CreateCustomerDto("Ana Souza", uniqueEmail, "11122233344"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var customerId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.GetAsync($"/api/v1/customers/{customerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("name").GetString().Should().Be("Ana Souza");
        json.GetProperty("data").GetProperty("email").GetString().Should().Be(uniqueEmail);
    }

    [Fact]
    public async Task GetCustomerById_NotFound_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/customers/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListCustomers_ReturnsPaginatedResult()
    {
        await Client.PostAsJsonAsync("/api/v1/customers",
            new CreateCustomerDto("Cliente 1", $"c1-{Guid.NewGuid():N}@test.com"));
        await Client.PostAsJsonAsync("/api/v1/customers",
            new CreateCustomerDto("Cliente 2", $"c2-{Guid.NewGuid():N}@test.com"));

        var response = await Client.GetAsync("/api/v1/customers?page=1&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("items").GetArrayLength().Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task AddPaymentMethod_CustomerNotExists_Returns404()
    {
        var pmDto = new AddPaymentMethodDto(
            Type: PaymentType.CREDIT_CARD,
            Token: "tok_test_123456",
            Gateway: PaymentProvider.STRIPE,
            First6: "411111",
            Last4: "1111",
            Brand: "Visa",
            Expiration: "12/2028",
            HolderName: "PM Customer",
            IsDefault: true
        );

        var response = await Client.PostAsJsonAsync($"/api/v1/customers/{Guid.NewGuid()}/payment-methods", pmDto);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateCustomer_WithoutAuth_Returns401()
    {
        var client = CreateUnauthenticatedClient();
        var dto = new CreateCustomerDto("Sem Auth", "noauth@test.com");

        var response = await client.PostAsJsonAsync("/api/v1/customers", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
