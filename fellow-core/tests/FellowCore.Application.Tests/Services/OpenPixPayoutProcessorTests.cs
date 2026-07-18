using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Payouts.Services;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Models;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class OpenPixPayoutProcessorTests
{
    private readonly IOpenPixApiClient _apiClient = Substitute.For<IOpenPixApiClient>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ILogger<OpenPixPayoutProcessor> _logger = Substitute.For<ILogger<OpenPixPayoutProcessor>>();
    private readonly OpenPixPayoutProcessor _sut;

    public OpenPixPayoutProcessorTests()
    {
        _configuration["OpenPix:AppId"].Returns("test-app-id");
        _sut = new OpenPixPayoutProcessor(_apiClient, _configuration, _logger);
    }

    [Fact]
    public async Task ProcessAsync_WithValidData_CallsAccountWithdraw()
    {
        var (payout, seller) = BuildPayoutAndSeller();

        _apiClient.WithdrawFromAccountAsync("test-app-id", "acc-123456", Arg.Any<OpenPixWithdrawRequest>())
            .Returns(BuildWithdrawResponse("E123456789"));

        var result = await _sut.ProcessAsync(payout, seller);

        result.Success.Should().BeTrue();
        result.TransactionId.Should().Be("E123456789");

        await _apiClient.Received(1).WithdrawFromAccountAsync(
            "test-app-id", "acc-123456",
            Arg.Is<OpenPixWithdrawRequest>(r => r.Value == 100000)); // 1000m * 100
    }

    [Fact]
    public async Task ProcessAsync_WithEndToEndId_ReturnsSuccess()
    {
        var (payout, seller) = BuildPayoutAndSeller();

        _apiClient.WithdrawFromAccountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OpenPixWithdrawRequest>())
            .Returns(BuildWithdrawResponse("E987654321"));

        var result = await _sut.ProcessAsync(payout, seller);

        result.Success.Should().BeTrue();
        result.TransactionId.Should().Be("E987654321");
    }

    [Fact]
    public async Task ProcessAsync_WhenSellerHasNoAccount_ReturnsFailure()
    {
        var payout = Payout.Create(Guid.NewGuid(), Guid.NewGuid(), 1000m).Value;
        var seller = Seller.Create(Guid.NewGuid(), "Test LTDA", "12345678000199", "test@test.com", "secret-32-chars-long-enough!!!!");

        var result = await _sut.ProcessAsync(payout, seller);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("conta BaaS");
    }

    [Fact]
    public async Task ProcessAsync_WhenApiThrows_PropagatesException()
    {
        var (payout, seller) = BuildPayoutAndSeller();

        _apiClient.WithdrawFromAccountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OpenPixWithdrawRequest>())
            .Throws(new PaymentProviderException("OPENPIX_WITHDRAW_ERROR", "Erro na API"));

        var act = () => _sut.ProcessAsync(payout, seller);

        await act.Should().ThrowAsync<PaymentProviderException>();
    }

    [Fact]
    public async Task ProcessAsync_WhenResponseHasNoTransaction_ReturnsFailure()
    {
        var (payout, seller) = BuildPayoutAndSeller();

        _apiClient.WithdrawFromAccountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OpenPixWithdrawRequest>())
            .Returns(new OpenPixWithdrawResponse());

        var result = await _sut.ProcessAsync(payout, seller);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("sem dados");
    }

    [Fact]
    public async Task ProcessAsync_WhenWithdrawHasNoTransaction_ReturnsFailure()
    {
        var (payout, seller) = BuildPayoutAndSeller();

        _apiClient.WithdrawFromAccountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OpenPixWithdrawRequest>())
            .Returns(new OpenPixWithdrawResponse(Withdraw: new OpenPixWithdrawData()));

        var result = await _sut.ProcessAsync(payout, seller);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("sem dados");
    }

    [Theory]
    [InlineData(100, 100, 101)]   // R$100 < R$500 → R$1 OpenPix + R$100 FellowCore = R$101
    [InlineData(499.99, 100, 101)] // R$499.99 < R$500 → R$1 OpenPix + R$100 FellowCore = R$101
    [InlineData(500, 100, 100)]    // R$500 >= R$500 → R$0 OpenPix + R$100 FellowCore = R$100
    [InlineData(1000, 100, 100)]   // R$1000 >= R$500 → R$0 OpenPix + R$100 FellowCore = R$100
    [InlineData(200, 50, 51)]      // R$200 < R$500 → R$1 OpenPix + R$50 FellowCore = R$51
    public void CalculateTotalFee_ReturnsCorrectFee(decimal amount, decimal fellowPayFee, decimal expectedTotal)
    {
        var result = OpenPixPayoutProcessor.CalculateTotalFee(amount, fellowPayFee);
        result.Should().Be(expectedTotal);
    }

    private static (Payout, Seller) BuildPayoutAndSeller()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var payout = Payout.Create(tenantId, sellerId, 1000m).Value;
        var seller = Seller.Create(tenantId, "Test Seller LTDA", "12345678000199", "seller@test.com",
            "secret-32-chars-long-enough!!!!",
            externalAccountId: "acc-123456",
            pixKey: "12345678000199");
        return (payout, seller);
    }

    private static OpenPixWithdrawResponse BuildWithdrawResponse(string? endToEndId = null)
    {
        return new OpenPixWithdrawResponse(
            Withdraw: new OpenPixWithdrawData(
                Account: new OpenPixWithdrawAccount(AccountId: "acc-123456", Balance: 500000),
                Transaction: new OpenPixWithdrawTransaction(
                    EndToEndId: endToEndId,
                    Value: 100000
                )
            )
        );
    }
}
