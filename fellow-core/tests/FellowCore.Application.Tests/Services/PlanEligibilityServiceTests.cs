using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Pricing.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class PlanEligibilityServiceTests
{
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly IPricingPlanRepository _pricingPlanRepository = Substitute.For<IPricingPlanRepository>();
    private readonly PlanEligibilityService _sut;

    public PlanEligibilityServiceTests()
    {
        _sut = new PlanEligibilityService(
            _sellerRepository,
            _transactionRepository,
            _pricingPlanRepository,
            Substitute.For<ILogger<PlanEligibilityService>>());
    }

    private static PricingPlan CreatePlan(string code, decimal monthlyFee = 0m)
        => PricingPlan.Create(
            code: code, name: $"Plano {code}", monthlyFee: monthlyFee,
            pixPercentFee: 1m, pixMinFee: 0.50m, pixMaxFee: null,
            debitPercentFee: 2m, debitFixedFee: 0m,
            creditCashPercentFee: 4m, creditCashFixedFee: 0.49m,
            creditInstallmentPercentFee: 6m, creditInstallmentFixedFee: 0.49m,
            boletoFixedFee: 3m,
            payoutFixedFee: 1m, includedPayoutsPerMonth: 0, extraPayoutFixedFee: 1m).Value;

    private static Seller CreateSeller(Guid tenantId, PricingPlan? plan = null)
    {
        var seller = Seller.Create(
            tenantId: tenantId,
            legalName: "Test Seller",
            document: "12345678901",
            email: "test@test.com",
            webhookSecret: "secret123456789012345678901234",
            pricingPlanId: plan?.Id);

        if (plan is not null)
        {
            typeof(Seller).GetProperty("PricingPlan")!
                .SetValue(seller, plan);
        }

        return seller;
    }

    [Fact]
    public async Task CheckEligibility_ShouldReturnNotEligible_WhenVolumeAndCountBelowThresholds()
    {
        var tenantId = Guid.NewGuid();
        var plan = CreatePlan("COMECE");
        var seller = CreateSeller(tenantId, plan);

        _sellerRepository.GetByIdWithPricingPlanAsync(tenantId, seller.Id).Returns(seller);
        _transactionRepository.GetSellerVolumeAsync(tenantId, seller.Id, Arg.Any<DateTime>())
            .Returns((2_000m, 10));

        var result = await _sut.CheckEligibilityAsync(tenantId, seller.Id);

        result.CanUpgrade.Should().BeFalse();
        result.CurrentPlanCode.Should().Be("COMECE");
        result.EligiblePlanCode.Should().BeNull();
    }

    [Fact]
    public async Task CheckEligibility_ShouldReturnCresca_WhenVolumeExceeds5000()
    {
        var tenantId = Guid.NewGuid();
        var comecePlan = CreatePlan("COMECE");
        var crescaPlan = CreatePlan("CRESCA", 49.90m);
        var seller = CreateSeller(tenantId, comecePlan);

        _sellerRepository.GetByIdWithPricingPlanAsync(tenantId, seller.Id).Returns(seller);
        _transactionRepository.GetSellerVolumeAsync(tenantId, seller.Id, Arg.Any<DateTime>())
            .Returns((6_000m, 20));
        _pricingPlanRepository.GetByCodeAsync("SCALA").Returns((PricingPlan?)null);
        _pricingPlanRepository.GetByCodeAsync("CRESCA").Returns(crescaPlan);

        var result = await _sut.CheckEligibilityAsync(tenantId, seller.Id);

        result.CanUpgrade.Should().BeTrue();
        result.CurrentPlanCode.Should().Be("COMECE");
        result.EligiblePlanCode.Should().Be("CRESCA");
        result.Reason.Should().Contain("R$6");
    }

    [Fact]
    public async Task CheckEligibility_ShouldReturnCresca_WhenTransactionCountExceeds50()
    {
        var tenantId = Guid.NewGuid();
        var comecePlan = CreatePlan("COMECE");
        var crescaPlan = CreatePlan("CRESCA", 49.90m);
        var seller = CreateSeller(tenantId, comecePlan);

        _sellerRepository.GetByIdWithPricingPlanAsync(tenantId, seller.Id).Returns(seller);
        _transactionRepository.GetSellerVolumeAsync(tenantId, seller.Id, Arg.Any<DateTime>())
            .Returns((3_000m, 55));
        _pricingPlanRepository.GetByCodeAsync("SCALA").Returns((PricingPlan?)null);
        _pricingPlanRepository.GetByCodeAsync("CRESCA").Returns(crescaPlan);

        var result = await _sut.CheckEligibilityAsync(tenantId, seller.Id);

        result.CanUpgrade.Should().BeTrue();
        result.CurrentPlanCode.Should().Be("COMECE");
        result.EligiblePlanCode.Should().Be("CRESCA");
        result.Reason.Should().Contain("55 transacoes");
    }

    [Fact]
    public async Task CheckEligibility_ShouldReturnScala_WhenVolumeExceeds100000()
    {
        var tenantId = Guid.NewGuid();
        var comecePlan = CreatePlan("COMECE");
        var scalaPlan = CreatePlan("SCALA", 499m);
        var seller = CreateSeller(tenantId, comecePlan);

        _sellerRepository.GetByIdWithPricingPlanAsync(tenantId, seller.Id).Returns(seller);
        _transactionRepository.GetSellerVolumeAsync(tenantId, seller.Id, Arg.Any<DateTime>())
            .Returns((150_000m, 200));
        _pricingPlanRepository.GetByCodeAsync("SCALA").Returns(scalaPlan);

        var result = await _sut.CheckEligibilityAsync(tenantId, seller.Id);

        result.CanUpgrade.Should().BeTrue();
        result.CurrentPlanCode.Should().Be("COMECE");
        result.EligiblePlanCode.Should().Be("SCALA");
        result.Reason.Should().Contain("SCALA");
    }

    [Fact]
    public async Task CheckEligibility_ShouldReturnCannotUpgrade_WhenAlreadyOnScala()
    {
        var tenantId = Guid.NewGuid();
        var scalaPlan = CreatePlan("SCALA", 499m);
        var seller = CreateSeller(tenantId, scalaPlan);

        _sellerRepository.GetByIdWithPricingPlanAsync(tenantId, seller.Id).Returns(seller);

        var result = await _sut.CheckEligibilityAsync(tenantId, seller.Id);

        result.CanUpgrade.Should().BeFalse();
        result.CurrentPlanCode.Should().Be("SCALA");
        result.EligiblePlanCode.Should().BeNull();
        result.Reason.Should().Contain("plano mais alto");
    }

    [Fact]
    public async Task CheckEligibility_ShouldReturnCannotUpgrade_WhenAlreadyOnBestPlanAndHighVolume()
    {
        var tenantId = Guid.NewGuid();
        var scalaPlan = CreatePlan("SCALA", 499m);
        var seller = CreateSeller(tenantId, scalaPlan);

        _sellerRepository.GetByIdWithPricingPlanAsync(tenantId, seller.Id).Returns(seller);

        // Even with very high volume, SCALA sellers cannot upgrade further
        var result = await _sut.CheckEligibilityAsync(tenantId, seller.Id);

        result.CanUpgrade.Should().BeFalse();
        result.CurrentPlanCode.Should().Be("SCALA");
        result.EligiblePlanCode.Should().BeNull();
    }

    [Fact]
    public async Task CheckEligibility_ShouldThrowNotFoundException_WhenSellerNotFound()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        _sellerRepository.GetByIdWithPricingPlanAsync(tenantId, sellerId).Returns((Seller?)null);

        var act = () => _sut.CheckEligibilityAsync(tenantId, sellerId);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task CheckEligibility_ShouldReturnScala_WhenCrescaSellerHasHighVolume()
    {
        // A seller on CRESCA with >= R$100k should be eligible for SCALA
        var tenantId = Guid.NewGuid();
        var crescaPlan = CreatePlan("CRESCA", 49.90m);
        var scalaPlan = CreatePlan("SCALA", 499m);
        var seller = CreateSeller(tenantId, crescaPlan);

        _sellerRepository.GetByIdWithPricingPlanAsync(tenantId, seller.Id).Returns(seller);
        _transactionRepository.GetSellerVolumeAsync(tenantId, seller.Id, Arg.Any<DateTime>())
            .Returns((120_000m, 300));
        _pricingPlanRepository.GetByCodeAsync("SCALA").Returns(scalaPlan);

        var result = await _sut.CheckEligibilityAsync(tenantId, seller.Id);

        result.CanUpgrade.Should().BeTrue();
        result.CurrentPlanCode.Should().Be("CRESCA");
        result.EligiblePlanCode.Should().Be("SCALA");
    }

    [Fact]
    public async Task CheckEligibility_ShouldDefaultToComece_WhenSellerHasNoPlan()
    {
        var tenantId = Guid.NewGuid();
        var seller = CreateSeller(tenantId, plan: null);

        _sellerRepository.GetByIdWithPricingPlanAsync(tenantId, seller.Id).Returns(seller);
        _transactionRepository.GetSellerVolumeAsync(tenantId, seller.Id, Arg.Any<DateTime>())
            .Returns((1_000m, 5));

        var result = await _sut.CheckEligibilityAsync(tenantId, seller.Id);

        result.CurrentPlanCode.Should().Be("COMECE");
        result.CanUpgrade.Should().BeFalse();
    }
}
