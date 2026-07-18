using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Pricing.Options;
using FellowCore.Application.Modules.Pricing.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FellowCore.Application.Tests.Services;

/// <summary>
/// Sprint 1.5: testes do PricingService 100% tier-based (sem PricingPlan).
/// Foco em: tier vigente determina fees, INFINITE com Rates=null cai pro
/// Seller.FeeSchedule, métricas só pra tier acima de SILVER.
/// </summary>
public class PricingServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private readonly ISellerRepository _sellerRepo = Substitute.For<ISellerRepository>();
    private readonly ISellerTierProfileRepository _tierRepo = Substitute.For<ISellerTierProfileRepository>();
    private readonly IProviderCostService _costSvc = Substitute.For<IProviderCostService>();
    private readonly IAppMetrics _metrics = Substitute.For<IAppMetrics>();

    private readonly PricingService _sut;

    public PricingServiceTests()
    {
        _sut = new PricingService(
            _sellerRepo,
            _tierRepo,
            _costSvc,
            Options.Create(new TierPricingOptions()),
            _metrics,
            Substitute.For<ILogger<PricingService>>());
    }

    private static Seller NewSeller(Guid tenantId, Guid sellerId) =>
        SellerTestFactory.NewSeller(tenantId, sellerId);

    private void SetupSellerAndTier(Guid sellerId, SellerTier? tierOrNull)
    {
        var seller = NewSeller(TenantId, sellerId);
        _sellerRepo.GetByIdAsync(TenantId, sellerId).Returns(seller);
        if (tierOrNull is SellerTier tier)
        {
            var profile = SellerTierProfile.Create(TenantId, sellerId, tier, 0m, DateTime.UtcNow);
            _tierRepo.GetBySellerIdAsync(TenantId, sellerId).Returns(profile);
        }
        else
        {
            _tierRepo.GetBySellerIdAsync(TenantId, sellerId).Returns((SellerTierProfile?)null);
        }
    }

    // ==========================================
    // Default tier resolution (no profile = SILVER)
    // ==========================================

    [Fact]
    public async Task NoProfile_UsesSilverRates_NoMetric()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, null);

        // PIX R$100 no SILVER: 2,90% × 100 + 0,47 = 3,37 (acima do min 0,50)
        var result = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.PIX, 1, 100m);

        result.PlatformFeeAmount.Should().Be(3.37m);
        result.SellerNetAmount.Should().Be(96.63m);
        _metrics.DidNotReceive().RecordTierDiscountApplied(Arg.Any<string>());
    }

    [Fact]
    public async Task SilverProfile_UsesSilverRates_NoMetric()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, SellerTier.SILVER);

        var result = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.PIX, 1, 100m);

        result.PlatformFeeAmount.Should().Be(3.37m);
        _metrics.DidNotReceive().RecordTierDiscountApplied(Arg.Any<string>());
    }

    // ==========================================
    // Higher tiers (rates + metric)
    // ==========================================

    [Fact]
    public async Task GoldProfile_UsesGoldRates_MetricFires()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, SellerTier.GOLD);

        // PIX R$100 no GOLD: 2,70% × 100 + 0,39 = 3,09
        var result = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.PIX, 1, 100m);

        result.PlatformFeeAmount.Should().Be(3.09m);
        _metrics.Received(1).RecordTierDiscountApplied("GOLD");
    }

    [Fact]
    public async Task BlackProfile_UsesBlackRates_MetricFires()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, SellerTier.BLACK);

        // PIX R$100 no BLACK: 2,40% × 100 + 0,19 = 2,59
        var result = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.PIX, 1, 100m);

        result.PlatformFeeAmount.Should().Be(2.59m);
        _metrics.Received(1).RecordTierDiscountApplied("BLACK");
    }

    // ==========================================
    // INFINITE fallback to FeeSchedule
    // ==========================================

    [Fact]
    public async Task InfiniteProfile_DefaultRatesNull_FallsBackToFeeSchedule()
    {
        // Defaults têm Rates[INFINITE]=null → cai pro Seller.FeeSchedule.
        var sellerId = Guid.NewGuid();
        var seller = Seller.Create(
            tenantId: TenantId, legalName: "Infinite Test", document: "12345678000190",
            email: "inf@test.local", webhookSecret: "wh_test", feePixIn: 1.00m);
        typeof(Seller).BaseType!.GetProperty("Id")!.SetValue(seller, sellerId);
        _sellerRepo.GetByIdAsync(TenantId, sellerId).Returns(seller);
        var profile = SellerTierProfile.Create(TenantId, sellerId, SellerTier.INFINITE, 0m, DateTime.UtcNow);
        _tierRepo.GetBySellerIdAsync(TenantId, sellerId).Returns(profile);

        var result = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.PIX, 1, 100m);

        // FeeSchedule.PIX = 1,00% × 100 = 1,00. (Não usa o min do tier).
        result.PlatformFeeAmount.Should().Be(1.00m);
        // Metric não dispara pra fallback FeeSchedule — fee não veio do tier.
        _metrics.DidNotReceive().RecordTierDiscountApplied(Arg.Any<string>());
    }

    [Fact]
    public async Task InfiniteProfile_WithCustomRates_UsesThoseRates()
    {
        var sellerId = Guid.NewGuid();
        var seller = NewSeller(TenantId, sellerId);
        _sellerRepo.GetByIdAsync(TenantId, sellerId).Returns(seller);
        var profile = SellerTierProfile.Create(TenantId, sellerId, SellerTier.INFINITE, 0m, DateTime.UtcNow);
        _tierRepo.GetBySellerIdAsync(TenantId, sellerId).Returns(profile);

        // Override: INFINITE no opts tem rates explícitas
        var opts = new TierPricingOptions();
        opts.Rates[SellerTier.INFINITE] = new TierFees
        {
            Pix = new PaymentTypeFees { Percent = 1.50m, Fixed = 0.10m },
            CreditCash = new PaymentTypeFees { Percent = 3m, Fixed = 0m },
            CreditInstallment = new PaymentTypeFees { Percent = 3m, Fixed = 0m },
            Debit = new PaymentTypeFees { Percent = 3m, Fixed = 0m },
            Boleto = new PaymentTypeFees { Percent = 0m, Fixed = 2m },
            Wallet = new PaymentTypeFees { Percent = 3m, Fixed = 0m },
        };
        var svcWithInfinite = new PricingService(
            _sellerRepo, _tierRepo, _costSvc,
            Options.Create(opts), _metrics,
            Substitute.For<ILogger<PricingService>>());

        var result = await svcWithInfinite.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.PIX, 1, 100m);

        result.PlatformFeeAmount.Should().Be(1.60m);  // 1,50% × 100 + 0,10
        _metrics.Received(1).RecordTierDiscountApplied("INFINITE");
    }

    // ==========================================
    // Payment type routing
    // ==========================================

    [Fact]
    public async Task CreditCard_Installments1_UsesCreditCashRates()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, SellerTier.SILVER);

        // SILVER CreditCash: 4,99% + 0,49. R$100 → 5,48.
        var result = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.CREDIT_CARD, 1, 100m);

        result.PlatformFeeAmount.Should().Be(5.48m);
    }

    [Fact]
    public async Task CreditCard_Installments6_UsesCreditInstallmentRates()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, SellerTier.SILVER);

        // SILVER CreditInstallment: 5,99% + 0,49. R$100 → 6,48.
        var result = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.CREDIT_CARD, 6, 100m);

        result.PlatformFeeAmount.Should().Be(6.48m);
    }

    [Fact]
    public async Task CreditCard_WithWallet_UsesWalletRates()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, SellerTier.SILVER);

        // SILVER Wallet: 4,99% + 0,49. R$100 → 5,48. (= CreditCash neste tier).
        var result = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.CREDIT_CARD, 1, 100m, walletType: "apple_pay");

        result.PlatformFeeAmount.Should().Be(5.48m);
    }

    [Fact]
    public async Task Boleto_UsesFixedFee()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, SellerTier.SILVER);

        // SILVER Boleto: R$3,49 fixo (Percent=0). Independente de amount.
        var r100 = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.BOLETO, 1, 100m);
        var r1000 = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.BOLETO, 1, 1_000m);

        r100.PlatformFeeAmount.Should().Be(3.49m);
        r1000.PlatformFeeAmount.Should().Be(3.49m);
    }

    [Fact]
    public async Task Debit_UsesDebitRates()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, SellerTier.SILVER);

        // SILVER Debit: 3,99% + 0,49. R$100 → 4,48.
        var result = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.DEBIT_CARD, 1, 100m);

        result.PlatformFeeAmount.Should().Be(4.48m);
    }

    // ==========================================
    // Min / Max enforcement
    // ==========================================

    [Fact]
    public async Task Pix_AmountBelowMin_AppliesMinFee()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, SellerTier.SILVER);

        // PIX R$1 no SILVER: 2,90% × 1 + 0,47 = 0,50 (exatamente o min). Vai bater min.
        // Em R$0,50 cents: 0,015 + 0,47 = 0,485 → min 0,50 kicks in.
        var result = await _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.PIX, 1, 0.50m);

        result.PlatformFeeAmount.Should().Be(0.50m); // min aplicado
    }

    // ==========================================
    // GetEffectiveMaxInstallments
    // ==========================================

    [Fact]
    public async Task MaxInstallments_NoOverride_ReturnsDefault12()
    {
        var sellerId = Guid.NewGuid();
        SetupSellerAndTier(sellerId, null);

        var max = await _sut.GetEffectiveMaxInstallmentsAsync(TenantId, sellerId);

        max.Should().Be(12);
    }

    [Fact]
    public async Task MaxInstallments_SellerOverride_WinsOverDefault()
    {
        var sellerId = Guid.NewGuid();
        var seller = NewSeller(TenantId, sellerId);
        seller.SetMaxInstallments(6);
        _sellerRepo.GetByIdAsync(TenantId, sellerId).Returns(seller);
        _tierRepo.GetBySellerIdAsync(TenantId, sellerId).Returns((SellerTierProfile?)null);

        var max = await _sut.GetEffectiveMaxInstallmentsAsync(TenantId, sellerId);

        max.Should().Be(6);
    }

    // ==========================================
    // NotFound
    // ==========================================

    [Fact]
    public async Task UnknownSeller_Throws()
    {
        var sellerId = Guid.NewGuid();
        _sellerRepo.GetByIdAsync(TenantId, sellerId).Returns((Seller?)null);

        await FluentActions.Invoking(() => _sut.CalculatePlatformFeeAsync(TenantId, sellerId, PaymentType.PIX, 1, 100m))
            .Should().ThrowAsync<NotFoundException>();
    }
}

