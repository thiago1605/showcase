using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.PaymentLinks.DTOs;
using FellowCore.Application.Modules.PaymentLinks.Services;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Tests.Flows;

/// <summary>
/// T17: Payment link flow tests.
/// Tests reserve + complete + fail rollback, expiration, and max usage enforcement.
/// </summary>
public class PaymentLinkFlowTests
{
    private readonly IPaymentLinkRepository _linkRepository = Substitute.For<IPaymentLinkRepository>();
    private readonly ITransactionService _transactionService = Substitute.For<ITransactionService>();
    private readonly ISplitRuleRepository _splitRuleRepository = Substitute.For<ISplitRuleRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly IPricingService _pricingService = Substitute.For<IPricingService>();
    private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration =
        Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>();
    private readonly PaymentLinkService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    public PaymentLinkFlowTests()
    {
        _sut = new PaymentLinkService(_linkRepository, _transactionService, _splitRuleRepository, _sellerRepository, _pricingService, _configuration);
    }

    // ── Reserve + Complete Flow ─────────────────────────────────────────

    [Fact]
    public async Task PayAsync_ShouldReserveUsage_ThenCreateTransaction_ThenComplete()
    {
        // Arrange
        var link = CreateActiveLink(maxUses: 3, usageCount: 0);
        var attempt = PaymentLinkUsageAttempt.CreateReserved(link.Id);

        _linkRepository.GetByTokenAsync(link.Token).Returns(link);
        _linkRepository.TryReserveUsageAsync(link.Id).Returns(attempt);

        var txId = Guid.NewGuid();
        _transactionService.CreateAsync(TenantId, Arg.Any<CreateTransactionDto>())
            .Returns(new TransactionResponseDto(txId, TransactionStatus.PROCESSING, 100m,
                new GatewayPaymentDetails("pi_test")));

        var payRequest = new PayPaymentLinkDto("John Doe", "12345678901", "john@test.com");

        // Act
        var result = await _sut.PayAsync(link.Token, payRequest);

        // Assert: usage reserved
        await _linkRepository.Received(1).TryReserveUsageAsync(link.Id);

        // Assert: transaction created with link details
        await _transactionService.Received(1).CreateAsync(TenantId, Arg.Is<CreateTransactionDto>(dto =>
            dto.Amount == 100m &&
            dto.SellerId == SellerId &&
            dto.PaymentType == PaymentType.CREDIT_CARD));

        // Assert: usage completed
        await _linkRepository.Received(1).CompleteUsageAttemptAsync(attempt.Id, txId);

        result.InternalId.Should().Be(txId);
        result.Status.Should().Be(TransactionStatus.PROCESSING);
    }

    // ── Reserve + Fail Rollback ────────────────────────────────────────

    [Fact]
    public async Task PayAsync_WhenTransactionFails_ShouldRollbackReservation()
    {
        // Arrange
        var link = CreateActiveLink(maxUses: 1, usageCount: 0);
        var attempt = PaymentLinkUsageAttempt.CreateReserved(link.Id);

        _linkRepository.GetByTokenAsync(link.Token).Returns(link);
        _linkRepository.TryReserveUsageAsync(link.Id).Returns(attempt);

        _transactionService.CreateAsync(TenantId, Arg.Any<CreateTransactionDto>())
            .Throws(new Exception("Payment provider error"));

        var payRequest = new PayPaymentLinkDto("John Doe", "12345678901", "john@test.com");

        // Act
        var act = () => _sut.PayAsync(link.Token, payRequest);

        // Assert: exception propagated
        await act.Should().ThrowAsync<Exception>().WithMessage("Payment provider error");

        // Assert: usage attempt rolled back (marked as FAILED)
        await _linkRepository.Received(1).FailUsageAttemptAsync(attempt.Id);

        // Assert: complete was NOT called
        await _linkRepository.DidNotReceive().CompleteUsageAttemptAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    // ── Expiration ─────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_WhenExpired_ShouldThrowBusinessException()
    {
        // Arrange: expired link
        var link = CreateActiveLink(maxUses: 10, usageCount: 0, expiresAt: DateTime.UtcNow.AddHours(-1));

        _linkRepository.GetByTokenAsync(link.Token).Returns(link);

        // Act
        var act = () => _sut.ResolveAsync(link.Token);

        // Assert
        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*expirado*");
    }

    [Fact]
    public async Task PayAsync_WhenExpired_ShouldThrowBusinessException()
    {
        // Arrange: expired link
        var link = CreateActiveLink(maxUses: 10, usageCount: 0, expiresAt: DateTime.UtcNow.AddHours(-1));

        _linkRepository.GetByTokenAsync(link.Token).Returns(link);

        var payRequest = new PayPaymentLinkDto("John Doe", "12345678901", "john@test.com");

        // Act
        var act = () => _sut.PayAsync(link.Token, payRequest);

        // Assert
        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*expirado*");
    }

    // ── Max Usage Enforcement ──────────────────────────────────────────

    [Fact]
    public async Task PayAsync_WhenMaxUsesReached_ShouldThrowWhenReserveFails()
    {
        // Arrange: link with max uses exhausted — TryReserveUsageAsync returns null
        var link = CreateActiveLink(maxUses: 1, usageCount: 0);

        _linkRepository.GetByTokenAsync(link.Token).Returns(link);
        _linkRepository.TryReserveUsageAsync(link.Id).Returns((PaymentLinkUsageAttempt?)null);

        var payRequest = new PayPaymentLinkDto("John Doe", "12345678901", "john@test.com");

        // Act
        var act = () => _sut.PayAsync(link.Token, payRequest);

        // Assert
        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*expirado*esgotado*");
    }

    [Fact]
    public async Task ResolveAsync_WhenMaxUsesExhausted_ShouldThrow()
    {
        // Arrange: link with usageCount >= maxUses
        var link = CreateActiveLink(maxUses: 2, usageCount: 2);

        _linkRepository.GetByTokenAsync(link.Token).Returns(link);

        // Act
        var act = () => _sut.ResolveAsync(link.Token);

        // Assert
        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*expirado*esgotado*");
    }

    // ── Link Not Found ─────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_WhenNotFound_ShouldThrowNotFoundException()
    {
        _linkRepository.GetByTokenAsync("nonexistent").Returns((PaymentLink?)null);

        var act = () => _sut.ResolveAsync("nonexistent");

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task PayAsync_WhenNotFound_ShouldThrowNotFoundException()
    {
        _linkRepository.GetByTokenAsync("nonexistent").Returns((PaymentLink?)null);

        var payRequest = new PayPaymentLinkDto("John Doe", "12345678901", "john@test.com");

        var act = () => _sut.PayAsync("nonexistent", payRequest);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── Deactivate ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeactivateAsync_ShouldMakeLinkInvalid()
    {
        var link = CreateActiveLink(maxUses: 10, usageCount: 0);
        _linkRepository.GetByIdAsync(TenantId, link.Id).Returns(link);

        await _sut.DeactivateAsync(TenantId, link.Id);

        link.Active.Should().BeFalse();
        link.IsValid().Should().BeFalse();
        _linkRepository.Received(1).Update(link);
        await _linkRepository.Received(1).SaveChangesAsync();
    }

    // ── Create Link ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ShouldReturnResponseWithToken()
    {
        var request = new CreatePaymentLinkDto(100m, PaymentType.PIX, 1, SellerId, "Test payment", 5);

        var result = await _sut.CreateAsync(TenantId, request, "https://api.fellowpay.com");

        result.Should().NotBeNull();
        result.Amount.Should().Be(100m);
        result.MaxUses.Should().Be(5);
        result.Token.Should().NotBeEmpty();
        result.Url.Should().Contain("pay/");

        _linkRepository.Received(1).Add(Arg.Any<PaymentLink>());
        await _linkRepository.Received(1).SaveChangesAsync();
    }

    // ── Valid Link Resolution ──────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_WhenValid_ShouldReturnLinkDetails()
    {
        var link = CreateActiveLink(maxUses: 5, usageCount: 2);
        _linkRepository.GetByTokenAsync(link.Token).Returns(link);

        var result = await _sut.ResolveAsync(link.Token);

        result.Amount.Should().Be(100m);
        result.PaymentType.Should().Be(PaymentType.CREDIT_CARD.ToString());
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private PaymentLink CreateActiveLink(int maxUses, int usageCount, DateTime? expiresAt = null)
    {
        var link = PaymentLink.Create(
            TenantId, 100m, PaymentType.CREDIT_CARD,
            installments: 1, sellerId: SellerId,
            description: "Test Link", maxUses: maxUses,
            expiresAt: expiresAt ?? DateTime.UtcNow.AddDays(7));

        // Set usageCount via reflection if > 0
        if (usageCount > 0)
        {
            typeof(PaymentLink).GetProperty("UsageCount")!.SetValue(link, usageCount);
            if (usageCount >= maxUses)
                typeof(PaymentLink).GetProperty("Active")!.SetValue(link, false);
        }

        return link;
    }
}
