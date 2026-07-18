using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Pricing.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class ProviderCostServiceTests
{
    private readonly IProviderCostScheduleRepository _repository = Substitute.For<IProviderCostScheduleRepository>();
    private readonly ProviderCostService _sut;

    public ProviderCostServiceTests()
    {
        var config = Substitute.For<IConfiguration>();
        config["ASPNETCORE_ENVIRONMENT"].Returns("Development");
        _sut = new ProviderCostService(_repository, config, Substitute.For<IAppMetrics>(), Substitute.For<ILogger<ProviderCostService>>());
    }

    // --- OpenPix PIX: 0.80%, min R$0.50, max R$5.00 ---

    private ProviderCostSchedule CreateOpenPixPixSchedule()
        => ProviderCostSchedule.Create(
            provider: PaymentProvider.OPENPIX,
            paymentType: PaymentType.PIX,
            percentFee: 0.80m,
            fixedFee: 0m,
            minFee: 0.50m,
            maxFee: 5.00m,
            description: "OpenPix PIX").Value;

    [Fact]
    public async Task OpenPixPix_SmallAmount_ShouldApplyMinFee()
    {
        // R$10.00 * 0.80% = R$0.08 -> below min R$0.50 -> should return R$0.50
        var schedule = CreateOpenPixPixSchedule();
        _repository.GetAsync(PaymentProvider.OPENPIX, PaymentType.PIX).Returns(schedule);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.OPENPIX, PaymentType.PIX, 10.00m);

        cost.Should().Be(0.50m);
    }

    [Fact]
    public async Task OpenPixPix_MediumAmount_ShouldApplyPercentFee()
    {
        // R$200.00 * 0.80% = R$1.60 -> within [0.50, 5.00] -> should return R$1.60
        var schedule = CreateOpenPixPixSchedule();
        _repository.GetAsync(PaymentProvider.OPENPIX, PaymentType.PIX).Returns(schedule);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.OPENPIX, PaymentType.PIX, 200.00m);

        cost.Should().Be(1.60m);
    }

    [Fact]
    public async Task OpenPixPix_LargeAmount_ShouldApplyMaxFee()
    {
        // R$1000.00 * 0.80% = R$8.00 -> above max R$5.00 -> should return R$5.00
        var schedule = CreateOpenPixPixSchedule();
        _repository.GetAsync(PaymentProvider.OPENPIX, PaymentType.PIX).Returns(schedule);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.OPENPIX, PaymentType.PIX, 1000.00m);

        cost.Should().Be(5.00m);
    }

    [Fact]
    public async Task OpenPixPix_ExactMinBoundary()
    {
        // R$62.50 * 0.80% = R$0.50 -> exactly at min -> should return R$0.50
        var schedule = CreateOpenPixPixSchedule();
        _repository.GetAsync(PaymentProvider.OPENPIX, PaymentType.PIX).Returns(schedule);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.OPENPIX, PaymentType.PIX, 62.50m);

        cost.Should().Be(0.50m);
    }

    [Fact]
    public async Task OpenPixPix_ExactMaxBoundary()
    {
        // R$625.00 * 0.80% = R$5.00 -> exactly at max -> should return R$5.00
        var schedule = CreateOpenPixPixSchedule();
        _repository.GetAsync(PaymentProvider.OPENPIX, PaymentType.PIX).Returns(schedule);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.OPENPIX, PaymentType.PIX, 625.00m);

        cost.Should().Be(5.00m);
    }

    // --- Stripe Card: 3.99% + R$0.39 ---

    private ProviderCostSchedule CreateStripeCardSchedule()
        => ProviderCostSchedule.Create(
            provider: PaymentProvider.STRIPE,
            paymentType: PaymentType.CREDIT_CARD,
            percentFee: 3.99m,
            fixedFee: 0.39m,
            minFee: 0m,
            maxFee: null,
            description: "Stripe national card").Value;

    [Fact]
    public async Task StripeCard_ShouldApplyPercentPlusFixed()
    {
        // R$100.00 * 3.99% + R$0.39 = R$3.99 + R$0.39 = R$4.38
        var schedule = CreateStripeCardSchedule();
        _repository.GetAsync(PaymentProvider.STRIPE, PaymentType.CREDIT_CARD).Returns(schedule);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.STRIPE, PaymentType.CREDIT_CARD, 100.00m);

        cost.Should().Be(4.38m);
    }

    [Fact]
    public async Task StripeCard_SmallAmount()
    {
        // R$10.00 * 3.99% + R$0.39 = R$0.399 + R$0.39 = R$0.789 -> rounded = R$0.79 (banker's: 0.789 rounds to 0.79)
        var schedule = CreateStripeCardSchedule();
        _repository.GetAsync(PaymentProvider.STRIPE, PaymentType.CREDIT_CARD).Returns(schedule);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.STRIPE, PaymentType.CREDIT_CARD, 10.00m);

        cost.Should().Be(0.79m);
    }

    [Fact]
    public async Task StripeCard_LargeAmount()
    {
        // R$5000.00 * 3.99% + R$0.39 = R$199.50 + R$0.39 = R$199.89
        var schedule = CreateStripeCardSchedule();
        _repository.GetAsync(PaymentProvider.STRIPE, PaymentType.CREDIT_CARD).Returns(schedule);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.STRIPE, PaymentType.CREDIT_CARD, 5000.00m);

        cost.Should().Be(199.89m);
    }

    // --- Stripe Boleto: R$3.45 flat ---

    private ProviderCostSchedule CreateStripeBoletoSchedule()
        => ProviderCostSchedule.Create(
            provider: PaymentProvider.STRIPE,
            paymentType: PaymentType.BOLETO,
            percentFee: 0m,
            fixedFee: 3.45m,
            minFee: 0m,
            maxFee: null,
            description: "Stripe boleto").Value;

    [Fact]
    public async Task StripeBoleto_ShouldReturnFlatFee()
    {
        // R$100.00 * 0% + R$3.45 = R$3.45
        var schedule = CreateStripeBoletoSchedule();
        _repository.GetAsync(PaymentProvider.STRIPE, PaymentType.BOLETO).Returns(schedule);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.STRIPE, PaymentType.BOLETO, 100.00m);

        cost.Should().Be(3.45m);
    }

    [Fact]
    public async Task StripeBoleto_ShouldReturnSameFlatFee_ForAnyAmount()
    {
        // R$5000.00 * 0% + R$3.45 = R$3.45
        var schedule = CreateStripeBoletoSchedule();
        _repository.GetAsync(PaymentProvider.STRIPE, PaymentType.BOLETO).Returns(schedule);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.STRIPE, PaymentType.BOLETO, 5000.00m);

        cost.Should().Be(3.45m);
    }

    // --- Sandbox returns 0 ---

    [Fact]
    public async Task Sandbox_ShouldReturnZero_WhenNoScheduleFound()
    {
        _repository.GetAsync(PaymentProvider.SANDBOX, PaymentType.PIX).Returns((ProviderCostSchedule?)null);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.SANDBOX, PaymentType.PIX, 100.00m);

        cost.Should().Be(0m);
    }

    [Fact]
    public async Task Sandbox_ShouldReturnZero_ForCreditCard()
    {
        _repository.GetAsync(PaymentProvider.SANDBOX, PaymentType.CREDIT_CARD).Returns((ProviderCostSchedule?)null);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.SANDBOX, PaymentType.CREDIT_CARD, 500.00m);

        cost.Should().Be(0m);
    }

    [Fact]
    public async Task Sandbox_ShouldReturnZero_ForBoleto()
    {
        _repository.GetAsync(PaymentProvider.SANDBOX, PaymentType.BOLETO).Returns((ProviderCostSchedule?)null);

        var cost = await _sut.CalculateProviderCostAsync(PaymentProvider.SANDBOX, PaymentType.BOLETO, 250.00m);

        cost.Should().Be(0m);
    }
}
