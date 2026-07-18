using System.Net;
using System.Net.Http.Json;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Domain.Enums;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;

namespace FellowCore.Integration.Tests;

public class ExportEndpointTests : IntegrationTestBase
{
    [Fact]
    public async Task ExportTransactionsCsv_Empty_ReturnsCsvWithHeaderOnly()
    {
        var response = await Client.GetAsync("/api/v1/transactions/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Id,Data,Tipo");
    }

    [Fact]
    public async Task ExportTransactionsCsv_WithData_ContainsTransaction()
    {
        var dto = new CreateTransactionDto(SellerId, 100m, PaymentType.PIX, 1, "Export test", new PayerDto("Maria", "12345678901", "maria@test.com"));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Content = JsonContent.Create(dto);
        await Client.SendAsync(request);

        var response = await Client.GetAsync("/api/v1/transactions/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Export test");
    }

    [Fact]
    public async Task ExportTransactionsPdf_ReturnsPdf()
    {
        var response = await Client.GetAsync("/api/v1/transactions/export?format=pdf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);
        // PDF magic bytes: %PDF
        bytes[0].Should().Be(0x25); // %
        bytes[1].Should().Be(0x50); // P
        bytes[2].Should().Be(0x44); // D
        bytes[3].Should().Be(0x46); // F
    }

    [Fact]
    public async Task ExportPayoutsCsv_ReturnsCsv()
    {
        var response = await Client.GetAsync("/api/v1/payouts/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Id,Data,Seller");
    }

    [Fact]
    public async Task ExportPayoutsPdf_ReturnsPdf()
    {
        var response = await Client.GetAsync("/api/v1/payouts/export?format=pdf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task ExportTransactions_Unauthenticated_Returns401()
    {
        var unauthClient = CreateUnauthenticatedClient();
        var response = await unauthClient.GetAsync("/api/v1/transactions/export");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExportTransactionsCsv_WithDateFilter_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/v1/transactions/export?format=csv&from=2020-01-01&to=2030-12-31");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
    }
}
