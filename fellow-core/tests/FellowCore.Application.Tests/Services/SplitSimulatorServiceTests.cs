using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Splits.DTOs;
using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Application.Modules.Splits.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Tests.Services;

public class SplitSimulatorServiceTests
{
    private readonly IPricingService _pricingService = Substitute.For<IPricingService>();
    private readonly IProviderCostService _providerCostService = Substitute.For<IProviderCostService>();
    private readonly ISplitRuleRepository _splitRuleRepository = Substitute.For<ISplitRuleRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly SplitSimulatorService _sut;

    public SplitSimulatorServiceTests()
    {
        _sut = new SplitSimulatorService(
            _pricingService,
            _providerCostService,
            _splitRuleRepository,
            _sellerRepository);
    }

    [Fact]
    public async Task SimulateAsync_NoSplits_ReturnsAllToPrimary()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var seller = BuildSeller(tenantId, sellerId);

        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);
        _pricingService.CalculatePlatformFeeAsync(tenantId, sellerId, PaymentType.PIX, 1, 1000m)
            .Returns(new PlatformFeeResult(50m, 950m));
        // Simulator resolve PIX → OPENPIX (preferência STRIPE do seller é incompatível com PIX).
        _providerCostService.CalculateProviderCostAsync(PaymentProvider.OPENPIX, PaymentType.PIX, 1000m)
            .Returns(8m);

        var request = new SimulateSplitRequest(sellerId, 1000m, PaymentType.PIX);
        var result = await _sut.SimulateAsync(tenantId, request);

        result.GrossAmount.Should().Be(1000m);
        result.PlatformFee.Should().Be(50m);
        result.ProviderCostEstimate.Should().Be(8m);
        result.PlatformMarginEstimate.Should().Be(42m);
        result.NetAmount.Should().Be(950m);
        result.Recipients.Should().BeEmpty();
        result.PrimaryResidual.SellerId.Should().Be(sellerId);
        result.PrimaryResidual.Amount.Should().Be(950m);
    }

    [Fact]
    public async Task SimulateAsync_WithExplicitSplits_AppliesPrimaryPaysFeesByDefault()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var seller = BuildSeller(tenantId, sellerId);

        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);
        _pricingService.CalculatePlatformFeeAsync(tenantId, sellerId, PaymentType.CREDIT_CARD, 1, 500m)
            .Returns(new PlatformFeeResult(25m, 475m));
        _providerCostService.CalculateProviderCostAsync(PaymentProvider.STRIPE, PaymentType.CREDIT_CARD, 500m)
            .Returns(20m);

        var request = new SimulateSplitRequest(
            sellerId, 500m, PaymentType.CREDIT_CARD,
            Splits: new List<SimulateSplitRecipient>
            {
                new(recipientId, Amount: 100m)
            });

        var result = await _sut.SimulateAsync(tenantId, request);

        // Recipient explícito: gross 100, fee 0 (primary paga), net 100.
        result.Recipients.Should().HaveCount(1);
        result.Recipients[0].SellerId.Should().Be(recipientId);
        result.Recipients[0].GrossShare.Should().Be(100m);
        result.Recipients[0].FeeShare.Should().Be(0m);
        result.Recipients[0].NetShare.Should().Be(100m);

        // Primary fica com residual (500 - 100 = 400) menos toda a fee (25) = 375.
        result.PrimaryResidual.SellerId.Should().Be(sellerId);
        result.PrimaryResidual.Amount.Should().Be(375m);
    }

    [Fact]
    public async Task SimulateAsync_NegativeMargin_AddsWarning()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var seller = BuildSeller(tenantId, sellerId);

        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);
        _pricingService.CalculatePlatformFeeAsync(tenantId, sellerId, PaymentType.PIX, 1, 100m)
            .Returns(new PlatformFeeResult(1m, 99m));
        _providerCostService.CalculateProviderCostAsync(PaymentProvider.OPENPIX, PaymentType.PIX, 100m)
            .Returns(5m);

        var request = new SimulateSplitRequest(sellerId, 100m, PaymentType.PIX);
        var result = await _sut.SimulateAsync(tenantId, request);

        result.PlatformMarginEstimate.Should().Be(-4m);
        result.Warnings.Should().ContainMatch("*negativa*");
    }

    [Fact]
    public async Task SimulateAsync_SellerNotFound_Throws()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns((Seller?)null);

        var request = new SimulateSplitRequest(sellerId, 1000m, PaymentType.PIX);
        var act = () => _sut.SimulateAsync(tenantId, request);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task SimulateAsync_ConflictingSplitsAndRuleId_Throws()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var seller = BuildSeller(tenantId, sellerId);

        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);
        _pricingService.CalculatePlatformFeeAsync(tenantId, sellerId, PaymentType.PIX, 1, 1000m)
            .Returns(new PlatformFeeResult(50m, 950m));
        _providerCostService.CalculateProviderCostAsync(PaymentProvider.STRIPE, PaymentType.PIX, 1000m)
            .Returns(8m);

        var request = new SimulateSplitRequest(
            sellerId, 1000m, PaymentType.PIX,
            SplitRuleId: Guid.NewGuid(),
            Splits: new List<SimulateSplitRecipient> { new(Guid.NewGuid(), Amount: 100m) });

        var act = () => _sut.SimulateAsync(tenantId, request);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*splits*splitRuleId*");
    }

    private static Seller BuildSeller(Guid tenantId, Guid sellerId)
    {
        var seller = Seller.Create(
            tenantId, "Test Seller", "12345678000190", "test@test.com",
            "webhook-secret", PaymentProvider.STRIPE);
        typeof(Seller).GetProperty("Id")!.SetValue(seller, sellerId);
        return seller;
    }
}
