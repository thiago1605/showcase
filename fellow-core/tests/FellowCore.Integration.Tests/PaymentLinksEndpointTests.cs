using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Modules.PaymentLinks.DTOs;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FellowCore.Integration.Tests;

public class PaymentLinksEndpointTests : IntegrationTestBase
{
    [Fact]
    public async Task CreatePaymentLink_ValidData_Returns201()
    {
        var dto = new CreatePaymentLinkDto(
            Amount: 150.00m,
            PaymentType: PaymentType.PIX,
            Installments: 1,
            SellerId: SellerId,
            Description: "Test payment link",
            MaxUses: 5,
            ExpiresAt: DateTime.UtcNow.AddDays(7));

        var response = await Client.PostAsJsonAsync("/api/v1/payment-links", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(150.00m);
        json.GetProperty("data").GetProperty("active").GetBoolean().Should().BeTrue();
        json.GetProperty("data").GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        // URL pública de checkout (front-end), não a rota interna da API.
        json.GetProperty("data").GetProperty("url").GetString().Should().Contain("/pay/");
    }

    [Fact]
    public async Task CreatePaymentLink_MissingAmount_Returns400()
    {
        var dto = new CreatePaymentLinkDto(
            Amount: 0m,
            PaymentType: PaymentType.PIX);

        var response = await Client.PostAsJsonAsync("/api/v1/payment-links", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListPaymentLinks_ReturnsOk()
    {
        await Client.PostAsJsonAsync("/api/v1/payment-links",
            new CreatePaymentLinkDto(Amount: 50.00m, PaymentType: PaymentType.PIX, SellerId: SellerId));
        await Client.PostAsJsonAsync("/api/v1/payment-links",
            new CreatePaymentLinkDto(Amount: 75.00m, PaymentType: PaymentType.PIX, SellerId: SellerId));

        var response = await Client.GetAsync("/api/v1/payment-links");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetArrayLength().Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task GetPaymentLink_NotFound_Returns404()
    {
        var response = await Client.GetAsync($"/api/v1/payment-links/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPaymentLink_AfterCreate_ReturnsOk()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/payment-links",
            new CreatePaymentLinkDto(
                Amount: 200.00m,
                PaymentType: PaymentType.CREDIT_CARD,
                Installments: 3,
                SellerId: SellerId,
                Description: "Get by id test"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var linkId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.GetAsync($"/api/v1/payment-links/{linkId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("id").GetString().Should().Be(linkId);
        json.GetProperty("data").GetProperty("amount").GetDecimal().Should().Be(200.00m);
        json.GetProperty("data").GetProperty("description").GetString().Should().Be("Get by id test");
    }

    [Fact]
    public async Task DeletePaymentLink_AfterCreate_Returns204()
    {
        var createResponse = await Client.PostAsJsonAsync("/api/v1/payment-links",
            new CreatePaymentLinkDto(
                Amount: 99.90m,
                PaymentType: PaymentType.PIX,
                SellerId: SellerId));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var linkId = created.GetProperty("data").GetProperty("id").GetString();

        var response = await Client.DeleteAsync($"/api/v1/payment-links/{linkId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CreatePaymentLink_Unauthenticated_Returns401()
    {
        var client = CreateUnauthenticatedClient();
        var dto = new CreatePaymentLinkDto(Amount: 100.00m, PaymentType: PaymentType.PIX);

        var response = await client.PostAsJsonAsync("/api/v1/payment-links", dto);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -- UsageAttempt tests --

    [Fact]
    public async Task PayLink_Success_CreatesCompletedAttemptWithTransactionId()
    {
        var token = await CreateLinkAndGetTokenAsync(maxUses: 3);

        var payResponse = await PayLinkAsync(token);
        payResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await payResponse.Content.ReadFromJsonAsync<JsonElement>();
        var txId = json.GetProperty("data").GetProperty("internalId").GetString();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempts = await db.PaymentLinkUsageAttempts
            .Where(a => a.TransactionId == Guid.Parse(txId!))
            .ToListAsync();

        attempts.Should().HaveCount(1);
        attempts[0].Status.Should().Be(UsageAttemptStatus.COMPLETED);
        attempts[0].CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task PayLink_Success_IncrementsUsageCount()
    {
        var (token, linkId) = await CreateLinkAndGetTokenAndIdAsync(maxUses: 5);

        var payResponse = await PayLinkAsync(token);
        payResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = await db.PaymentLinks.AsNoTracking().FirstAsync(l => l.Id == linkId);
        link.UsageCount.Should().Be(1);
        link.Active.Should().BeTrue();
    }

    [Fact]
    public async Task PayLink_MaxUses1_SecondPayReturns400()
    {
        var token = await CreateLinkAndGetTokenAsync(maxUses: 1);

        var first = await PayLinkAsync(token);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await PayLinkAsync(token);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PayLink_MaxUses1_DeactivatesLinkAfterSingleUse()
    {
        var (token, linkId) = await CreateLinkAndGetTokenAndIdAsync(maxUses: 1);

        await PayLinkAsync(token);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = await db.PaymentLinks.AsNoTracking().FirstAsync(l => l.Id == linkId);
        link.UsageCount.Should().Be(1);
        link.Active.Should().BeFalse();
    }

    [Fact]
    public async Task PayLink_MultipleSuccessfulPays_EachCreatesOwnAttempt()
    {
        var (token, linkId) = await CreateLinkAndGetTokenAndIdAsync(maxUses: 3);

        await PayLinkAsync(token);
        await PayLinkAsync(token);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attempts = await db.PaymentLinkUsageAttempts
            .Where(a => a.PaymentLinkId == linkId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();

        attempts.Should().HaveCount(2);
        attempts.Should().AllSatisfy(a =>
        {
            a.Status.Should().Be(UsageAttemptStatus.COMPLETED);
            a.TransactionId.Should().NotBeNull();
        });

        // Each attempt should reference a different transaction
        attempts[0].TransactionId!.Value.Should().NotBe(attempts[1].TransactionId!.Value);
    }

    [Fact]
    public async Task PayLink_UsageCountNeverGoesNegative()
    {
        var (token, linkId) = await CreateLinkAndGetTokenAndIdAsync(maxUses: 1);

        await PayLinkAsync(token);

        // Try to pay again — should fail
        await PayLinkAsync(token);
        await PayLinkAsync(token);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var link = await db.PaymentLinks.AsNoTracking().FirstAsync(l => l.Id == linkId);
        link.UsageCount.Should().BeGreaterOrEqualTo(0);
        link.UsageCount.Should().BeLessOrEqualTo(link.MaxUses ?? int.MaxValue);
    }

    // -- UsageAttempt lifecycle edge cases --

    [Fact]
    public async Task UsageAttempt_DoubleComplete_SecondReturnsFalse()
    {
        var (_, linkId) = await CreateLinkAndGetTokenAndIdAsync(maxUses: 3);

        using var scope = Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPaymentLinkRepository>();

        var attempt = await repo.TryReserveUsageAsync(linkId);
        attempt.Should().NotBeNull();

        var first = await repo.CompleteUsageAttemptAsync(attempt!.Id, Guid.NewGuid());
        first.Should().BeTrue();

        var second = await repo.CompleteUsageAttemptAsync(attempt.Id, Guid.NewGuid());
        second.Should().BeFalse();
    }

    [Fact]
    public async Task UsageAttempt_DoubleFail_SecondIsNoOp()
    {
        var (_, linkId) = await CreateLinkAndGetTokenAndIdAsync(maxUses: 3);

        using var scope = Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPaymentLinkRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var attempt = await repo.TryReserveUsageAsync(linkId);
        attempt.Should().NotBeNull();

        // After reserve, UsageCount = 1
        await repo.FailUsageAttemptAsync(attempt!.Id);
        // After first fail, UsageCount = 0

        await repo.FailUsageAttemptAsync(attempt.Id);
        // Second fail should be no-op, UsageCount stays 0

        var link = await db.PaymentLinks.AsNoTracking().FirstAsync(l => l.Id == linkId);
        link.UsageCount.Should().Be(0);
        link.Active.Should().BeTrue();
    }

    [Fact]
    public async Task UsageAttempt_CompleteAfterFail_IsNoOp()
    {
        var (_, linkId) = await CreateLinkAndGetTokenAndIdAsync(maxUses: 3);

        using var scope = Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPaymentLinkRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var attempt = await repo.TryReserveUsageAsync(linkId);
        attempt.Should().NotBeNull();

        await repo.FailUsageAttemptAsync(attempt!.Id);

        // Try to complete a failed attempt — should return false
        var completed = await repo.CompleteUsageAttemptAsync(attempt.Id, Guid.NewGuid());
        completed.Should().BeFalse();

        // UsageCount should be 0 (failed attempt was rolled back)
        var link = await db.PaymentLinks.AsNoTracking().FirstAsync(l => l.Id == linkId);
        link.UsageCount.Should().Be(0);
    }

    [Fact]
    public async Task UsageAttempt_FailAfterComplete_IsNoOp()
    {
        var (_, linkId) = await CreateLinkAndGetTokenAndIdAsync(maxUses: 3);

        using var scope = Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPaymentLinkRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var attempt = await repo.TryReserveUsageAsync(linkId);
        attempt.Should().NotBeNull();

        await repo.CompleteUsageAttemptAsync(attempt!.Id, Guid.NewGuid());

        // Try to fail a completed attempt — should be no-op
        await repo.FailUsageAttemptAsync(attempt.Id);

        // UsageCount should remain 1 (completed attempt not rolled back)
        var link = await db.PaymentLinks.AsNoTracking().FirstAsync(l => l.Id == linkId);
        link.UsageCount.Should().Be(1);
    }

    [Fact]
    public async Task UsageAttempt_FailedAttempt_AllowsNewReservation()
    {
        var (_, linkId) = await CreateLinkAndGetTokenAndIdAsync(maxUses: 1);

        using var scope = Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPaymentLinkRepository>();

        // Reserve and fail
        var attempt1 = await repo.TryReserveUsageAsync(linkId);
        attempt1.Should().NotBeNull();
        await repo.FailUsageAttemptAsync(attempt1!.Id);

        // A new reservation should succeed since the first was rolled back
        var attempt2 = await repo.TryReserveUsageAsync(linkId);
        attempt2.Should().NotBeNull();
        attempt2!.Id.Should().NotBe(attempt1.Id);
    }

    // -- Helpers --

    private async Task<string> CreateLinkAndGetTokenAsync(int maxUses = 1)
    {
        var (token, _) = await CreateLinkAndGetTokenAndIdAsync(maxUses);
        return token;
    }

    private async Task<(string Token, Guid Id)> CreateLinkAndGetTokenAndIdAsync(int maxUses = 1)
    {
        var dto = new CreatePaymentLinkDto(
            Amount: 50.00m,
            PaymentType: PaymentType.PIX,
            Installments: 1,
            SellerId: SellerId,
            MaxUses: maxUses);

        var response = await Client.PostAsJsonAsync("/api/v1/payment-links", dto);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = json.GetProperty("data").GetProperty("token").GetString()!;
        var id = Guid.Parse(json.GetProperty("data").GetProperty("id").GetString()!);
        return (token, id);
    }

    private async Task<HttpResponseMessage> PayLinkAsync(string token)
    {
        var unauthClient = CreateUnauthenticatedClient();
        var pay = new PayPaymentLinkDto(
            PayerName: "Test Payer",
            PayerDocument: "12345678901",
            PayerEmail: "payer@test.com");
        return await unauthClient.PostAsJsonAsync($"/api/v1/payment-links/pay/{token}", pay);
    }
}
