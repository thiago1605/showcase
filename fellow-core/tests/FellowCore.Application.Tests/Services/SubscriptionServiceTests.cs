using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Subscriptions.DTOs;
using FellowCore.Application.Modules.Subscriptions.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class SubscriptionServiceTests
{
    private readonly ISubscriptionRepository _subscriptionRepository = Substitute.For<ISubscriptionRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ILogger<SubscriptionService> _logger = Substitute.For<ILogger<SubscriptionService>>();
    private readonly SubscriptionService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();

    public SubscriptionServiceTests()
    {
        _sut = new SubscriptionService(_subscriptionRepository, _sellerRepository, _logger);
    }

    #region CreateAsync

    [Fact]
    public async Task CreateAsync_ShouldThrowNotFoundException_WhenSellerNotFound()
    {
        _sellerRepository.GetByIdAsync(TenantId, Arg.Any<Guid>()).Returns((Seller?)null);

        var request = BuildCreateRequest();
        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Seller*");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowBusinessException_WhenSellerNotActive()
    {
        var seller = BuildSeller();
        seller.Suspend();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);

        var request = BuildCreateRequest(sellerId: seller.Id);
        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*nao esta ativo*");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowValidationException_WhenAmountIsZero()
    {
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);

        var request = new CreateSubscriptionDto(seller.Id, 0m, "Plano Mensal", BillingInterval.MONTHLY);
        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*valor*");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowValidationException_WhenDescriptionIsEmpty()
    {
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);

        var request = new CreateSubscriptionDto(seller.Id, 99.90m, "", BillingInterval.MONTHLY);
        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<ValidationException>()
            .WithMessage("*obrigat*");
    }

    [Fact]
    public async Task CreateAsync_ShouldCreateAndPersistSubscription()
    {
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);

        var request = BuildCreateRequest(sellerId: seller.Id);
        var result = await _sut.CreateAsync(TenantId, request);

        result.Should().NotBeNull();
        result.SellerId.Should().Be(seller.Id);
        result.Amount.Should().Be(99.90m);
        result.Status.Should().Be(SubscriptionStatus.ACTIVE);
        result.Interval.Should().Be(BillingInterval.MONTHLY);

        _subscriptionRepository.Received(1).Add(Arg.Any<Subscription>());
        await _subscriptionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task CreateAsync_WithMaxCycles_ShouldSetMaxCycles()
    {
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);

        var request = new CreateSubscriptionDto(seller.Id, 50m, "Plano 12x", BillingInterval.MONTHLY, MaxCycles: 12);
        var result = await _sut.CreateAsync(TenantId, request);

        result.Should().NotBeNull();
        result.Amount.Should().Be(50m);
        _subscriptionRepository.Received(1).Add(Arg.Any<Subscription>());
    }

    [Fact]
    public async Task CreateAsync_WithCustomStartDate_ShouldUseProvidedDate()
    {
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);

        var futureDate = DateTime.UtcNow.AddDays(30);
        var request = new CreateSubscriptionDto(seller.Id, 99.90m, "Plano Mensal", BillingInterval.MONTHLY, StartDate: futureDate);
        var result = await _sut.CreateAsync(TenantId, request);

        result.Should().NotBeNull();
        result.NextBillingDate.Should().BeCloseTo(futureDate, TimeSpan.FromSeconds(2));
    }

    #endregion

    #region GetByIdAsync

    [Fact]
    public async Task GetByIdAsync_ShouldThrowNotFoundException_WhenNotFound()
    {
        _subscriptionRepository.GetByIdAsync(TenantId, Arg.Any<Guid>()).Returns((Subscription?)null);

        var act = () => _sut.GetByIdAsync(TenantId, Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*não encontrada*");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnDetail_WhenFound()
    {
        var subscription = BuildSubscription();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        var result = await _sut.GetByIdAsync(TenantId, subscription.Id);

        result.Should().NotBeNull();
        result.Id.Should().Be(subscription.Id);
        result.Amount.Should().Be(subscription.Amount);
    }

    #endregion

    #region ListAsync

    [Fact]
    public async Task ListAsync_ShouldReturnPagedResult()
    {
        var sub1 = BuildSubscription();
        var sub2 = BuildSubscription();
        _subscriptionRepository.GetPagedAsync(TenantId, 0, 20, null, null)
            .Returns((new List<Subscription> { sub1, sub2 }.AsReadOnly(), 2));

        var result = await _sut.ListAsync(TenantId, new SubscriptionFilterDto());

        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEmptyResult_WhenNoSubscriptions()
    {
        _subscriptionRepository.GetPagedAsync(TenantId, 0, 20, null, null)
            .Returns((new List<Subscription>().AsReadOnly(), 0));

        var result = await _sut.ListAsync(TenantId, new SubscriptionFilterDto());

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    #endregion

    #region CancelAsync

    [Fact]
    public async Task CancelAsync_ShouldThrowNotFoundException_WhenNotFound()
    {
        _subscriptionRepository.GetByIdAsync(TenantId, Arg.Any<Guid>()).Returns((Subscription?)null);

        var act = () => _sut.CancelAsync(TenantId, Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task CancelAsync_ShouldCancelSubscription_WhenActive()
    {
        var subscription = BuildSubscription();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        var result = await _sut.CancelAsync(TenantId, subscription.Id);

        result.Status.Should().Be(SubscriptionStatus.CANCELED);
        _subscriptionRepository.Received(1).Update(subscription);
        await _subscriptionRepository.Received(1).SaveChangesAsync();
    }

    #endregion

    #region PauseAsync

    [Fact]
    public async Task PauseAsync_ShouldThrowNotFoundException_WhenNotFound()
    {
        _subscriptionRepository.GetByIdAsync(TenantId, Arg.Any<Guid>()).Returns((Subscription?)null);

        var act = () => _sut.PauseAsync(TenantId, Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task PauseAsync_ShouldPauseSubscription_WhenActive()
    {
        var subscription = BuildSubscription();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        var result = await _sut.PauseAsync(TenantId, subscription.Id);

        result.Status.Should().Be(SubscriptionStatus.PAUSED);
        _subscriptionRepository.Received(1).Update(subscription);
        await _subscriptionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task PauseAsync_ShouldThrowBusinessException_WhenNotActive()
    {
        var subscription = BuildSubscription();
        subscription.Cancel();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        var act = () => _sut.PauseAsync(TenantId, subscription.Id);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*ativas*");
    }

    #endregion

    #region ResumeAsync

    [Fact]
    public async Task ResumeAsync_ShouldThrowNotFoundException_WhenNotFound()
    {
        _subscriptionRepository.GetByIdAsync(TenantId, Arg.Any<Guid>()).Returns((Subscription?)null);

        var act = () => _sut.ResumeAsync(TenantId, Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ResumeAsync_ShouldResumeSubscription_WhenPaused()
    {
        var subscription = BuildSubscription();
        subscription.Pause();
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        var result = await _sut.ResumeAsync(TenantId, subscription.Id);

        result.Status.Should().Be(SubscriptionStatus.ACTIVE);
        _subscriptionRepository.Received(1).Update(subscription);
        await _subscriptionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ResumeAsync_ShouldThrowBusinessException_WhenNotPaused()
    {
        var subscription = BuildSubscription();
        // subscription is ACTIVE, not PAUSED
        _subscriptionRepository.GetByIdAsync(TenantId, subscription.Id).Returns(subscription);

        var act = () => _sut.ResumeAsync(TenantId, subscription.Id);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*pausadas*");
    }

    #endregion

    #region Billing Cycle and MaxCycles

    [Fact]
    public void AdvanceCycle_ShouldIncrementCycleCount()
    {
        var subscription = BuildSubscription();
        subscription.CycleCount.Should().Be(0);

        subscription.AdvanceCycle();

        subscription.CycleCount.Should().Be(1);
    }

    [Fact]
    public void AdvanceCycle_ShouldSetNextBillingDate_ForMonthlyInterval()
    {
        var subscription = BuildSubscription();
        var originalNext = subscription.NextBillingDate;

        subscription.AdvanceCycle();

        subscription.NextBillingDate.Should().BeCloseTo(originalNext.AddMonths(1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AdvanceCycle_ShouldExpire_WhenMaxCyclesReached()
    {
        var result = Subscription.Create(TenantId, Guid.NewGuid(), 50m, "Plano 2x",
            BillingInterval.MONTHLY, maxCycles: 2);
        var subscription = result.Value;

        subscription.AdvanceCycle(); // Cycle 1
        subscription.Status.Should().Be(SubscriptionStatus.ACTIVE);

        subscription.AdvanceCycle(); // Cycle 2 — hits MaxCycles
        subscription.Status.Should().Be(SubscriptionStatus.EXPIRED);
        subscription.EndDate.Should().NotBeNull();
    }

    [Fact]
    public void AdvanceCycle_ShouldNotExpire_WhenMaxCyclesIsNull()
    {
        var subscription = BuildSubscription(); // MaxCycles = null

        for (int i = 0; i < 100; i++)
            subscription.AdvanceCycle();

        subscription.Status.Should().Be(SubscriptionStatus.ACTIVE);
        subscription.CycleCount.Should().Be(100);
    }

    #endregion

    #region Tenant Isolation

    [Fact]
    public async Task GetByIdAsync_ShouldUseTenantId_ForIsolation()
    {
        var otherTenantId = Guid.NewGuid();
        var subscriptionId = Guid.NewGuid();

        _subscriptionRepository.GetByIdAsync(otherTenantId, subscriptionId).Returns((Subscription?)null);

        var act = () => _sut.GetByIdAsync(otherTenantId, subscriptionId);

        await act.Should().ThrowAsync<NotFoundException>();
        await _subscriptionRepository.Received(1).GetByIdAsync(otherTenantId, subscriptionId);
    }

    #endregion

    #region Helpers

    private static CreateSubscriptionDto BuildCreateRequest(Guid? sellerId = null) =>
        new(sellerId ?? Guid.NewGuid(), 99.90m, "Plano Mensal", BillingInterval.MONTHLY);

    private static Seller BuildSeller()
    {
        return Seller.Create(TenantId, "Loja Teste", "12345678000190", "loja@test.com", "secret123");
    }

    private static Subscription BuildSubscription()
    {
        var result = Subscription.Create(TenantId, Guid.NewGuid(), 99.90m, "Plano Mensal", BillingInterval.MONTHLY);
        return result.Value;
    }

    #endregion
}
