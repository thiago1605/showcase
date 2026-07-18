using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.PaymentLinks.DTOs;
using FellowCore.Application.Modules.PaymentLinks.Interfaces;
using FellowCore.Application.Modules.PaymentLinks.Services;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Tests.Services;

public class PaymentLinkServiceTests
{
    private readonly IPaymentLinkRepository _linkRepo = Substitute.For<IPaymentLinkRepository>();
    private readonly ITransactionService _txService = Substitute.For<ITransactionService>();
    private readonly ISplitRuleRepository _splitRuleRepo = Substitute.For<ISplitRuleRepository>();
    private readonly ISellerRepository _sellerRepo = Substitute.For<ISellerRepository>();
    private readonly IPricingService _pricing = Substitute.For<IPricingService>();
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config =
        Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>();
    private readonly PaymentLinkService _sut;

    public PaymentLinkServiceTests()
    {
        _sut = new PaymentLinkService(_linkRepo, _txService, _splitRuleRepo, _sellerRepo, _pricing, _config);
    }

    [Fact]
    public async Task PayAsync_Success_ReservesCompletesAttempt()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, sellerId: Guid.NewGuid());
        _linkRepo.GetByTokenAsync(link.Token).Returns(link);

        var attempt = PaymentLinkUsageAttempt.CreateReserved(link.Id);
        _linkRepo.TryReserveUsageAsync(link.Id).Returns(attempt);

        var txResponse = new TransactionResponseDto(
            Guid.NewGuid(), TransactionStatus.PROCESSING, 100m,
            new GatewayPaymentDetails("fake-provider-id"));
        _txService.CreateAsync(link.TenantId, Arg.Any<CreateTransactionDto>()).Returns(txResponse);

        var pay = new PayPaymentLinkDto("Payer", "12345678901", "payer@test.com");
        var result = await _sut.PayAsync(link.Token, pay);

        result.Should().Be(txResponse);
        await _linkRepo.Received(1).CompleteUsageAttemptAsync(attempt.Id, txResponse.InternalId);
        await _linkRepo.DidNotReceive().FailUsageAttemptAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task PayAsync_TransactionFails_FailsAttemptAndRethrows()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, sellerId: Guid.NewGuid());
        _linkRepo.GetByTokenAsync(link.Token).Returns(link);

        var attempt = PaymentLinkUsageAttempt.CreateReserved(link.Id);
        _linkRepo.TryReserveUsageAsync(link.Id).Returns(attempt);

        _txService.CreateAsync(Arg.Any<Guid>(), Arg.Any<CreateTransactionDto>())
            .ThrowsAsync(new InvalidOperationException("Provider error"));

        var pay = new PayPaymentLinkDto("Payer", "12345678901", "payer@test.com");

        var act = () => _sut.PayAsync(link.Token, pay);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Provider error");

        await _linkRepo.Received(1).FailUsageAttemptAsync(attempt.Id);
        await _linkRepo.DidNotReceive().CompleteUsageAttemptAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task PayAsync_LinkExhausted_ReserveReturnsNull_ThrowsBusinessException()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, sellerId: Guid.NewGuid());
        _linkRepo.GetByTokenAsync(link.Token).Returns(link);
        _linkRepo.TryReserveUsageAsync(link.Id).Returns((PaymentLinkUsageAttempt?)null);

        var pay = new PayPaymentLinkDto("Payer", "12345678901", "payer@test.com");

        var act = () => _sut.PayAsync(link.Token, pay);
        await act.Should().ThrowAsync<BusinessException>();

        await _txService.DidNotReceive().CreateAsync(Arg.Any<Guid>(), Arg.Any<CreateTransactionDto>());
    }

    [Fact]
    public async Task PayAsync_InvalidLink_ThrowsBusinessException()
    {
        // Create an expired link
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX,
            expiresAt: DateTime.UtcNow.AddDays(-1));
        _linkRepo.GetByTokenAsync(link.Token).Returns(link);

        var pay = new PayPaymentLinkDto("Payer", "12345678901", "payer@test.com");

        var act = () => _sut.PayAsync(link.Token, pay);
        await act.Should().ThrowAsync<BusinessException>();

        await _linkRepo.DidNotReceive().TryReserveUsageAsync(Arg.Any<Guid>());
    }
}
