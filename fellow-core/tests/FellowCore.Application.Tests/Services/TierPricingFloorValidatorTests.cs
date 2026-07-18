using FluentAssertions;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Pricing.Options;
using FellowCore.Domain.Enums;
using FellowCore.Infrastructure.Pricing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FellowCore.Application.Tests.Services;

/// <summary>
/// Cobertura do hosted-service que valida a tabela de tier × payment type
/// contra o floor de margem (Sprint 1.5 — sem mais PricingPlan).
/// </summary>
public class TierPricingFloorValidatorTests
{
    private readonly IProviderCostService _costSvc = Substitute.For<IProviderCostService>();

    private TierPricingFloorValidator BuildValidator(TierPricingOptions opts)
    {
        // Como o validator agora usa IServiceScopeFactory pra resolver IProviderCostService
        // (singleton/scoped boundary), montamos um mini DI provider só pro teste.
        var sp = new ServiceCollection()
            .AddScoped(_ => _costSvc)
            .BuildServiceProvider();
        return new TierPricingFloorValidator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(opts),
            NullLogger<TierPricingFloorValidator>.Instance);
    }

    /// <summary>Helper: tier com fees uniformes (PIX percent dado, outros zerados).</summary>
    private static TierFees TierWithPix(decimal pixPercent, decimal pixFixed = 0m, decimal? pixMin = null) =>
        new()
        {
            Pix = new PaymentTypeFees { Percent = pixPercent, Fixed = pixFixed, Min = pixMin },
            CreditCash = new PaymentTypeFees { Percent = 10m, Fixed = 0m }, // gordo o suficiente pra não violar
            CreditInstallment = new PaymentTypeFees { Percent = 10m, Fixed = 0m },
            Debit = new PaymentTypeFees { Percent = 10m, Fixed = 0m },
            Boleto = new PaymentTypeFees { Percent = 0m, Fixed = 10m },
            Wallet = new PaymentTypeFees { Percent = 10m, Fixed = 0m },
        };

    [Fact]
    public async Task DefaultRates_PassFloor()
    {
        // Provider costs realistas: PIX 0.80% min R$0.50, Stripe 3.99%+0.39, boleto R$3.45 fixo.
        _costSvc.CalculateProviderCostAsync(PaymentProvider.OPENPIX, PaymentType.PIX, Arg.Any<decimal>())
            .Returns(call => Math.Max(0.50m, ((decimal)call[2]) * 0.008m));
        _costSvc.CalculateProviderCostAsync(PaymentProvider.STRIPE, Arg.Any<PaymentType>(), Arg.Any<decimal>())
            .Returns(call => ((decimal)call[2]) * 0.0399m + 0.39m);
        _costSvc.CalculateProviderCostAsync(PaymentProvider.STRIPE, PaymentType.BOLETO, Arg.Any<decimal>())
            .Returns(3.45m);

        var validator = BuildValidator(new TierPricingOptions()); // defaults

        // Não throws — todos os tiers passam o floor 30%.
        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConfigViolatesFloor_AndEnforceTrue_Throws()
    {
        // Tier com PIX 0.5% (vs custo 0.8%) → margem negativa.
        var opts = new TierPricingOptions
        {
            Rates = new Dictionary<SellerTier, TierFees?>
            {
                [SellerTier.SILVER] = TierWithPix(pixPercent: 0.5m),
            },
            EnforceFloorAtStartup = true
        };
        _costSvc.CalculateProviderCostAsync(PaymentProvider.OPENPIX, PaymentType.PIX, Arg.Any<decimal>())
            .Returns(call => Math.Max(0.50m, ((decimal)call[2]) * 0.008m));
        _costSvc.CalculateProviderCostAsync(PaymentProvider.STRIPE, Arg.Any<PaymentType>(), Arg.Any<decimal>())
            .Returns(call => ((decimal)call[2]) * 0.0399m + 0.39m);

        await FluentActions.Invoking(() => BuildValidator(opts).StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*viola floor*");
    }

    [Fact]
    public async Task ConfigViolatesFloor_AndEnforceFalse_DoesNotThrow()
    {
        var opts = new TierPricingOptions
        {
            Rates = new Dictionary<SellerTier, TierFees?> { [SellerTier.SILVER] = TierWithPix(pixPercent: 0.5m) },
            EnforceFloorAtStartup = false
        };
        _costSvc.CalculateProviderCostAsync(Arg.Any<PaymentProvider>(), Arg.Any<PaymentType>(), Arg.Any<decimal>())
            .Returns(call => ((decimal)call[2]) * 0.01m + 0.50m);

        await BuildValidator(opts).StartAsync(CancellationToken.None);  // só warning, não throws
    }

    [Fact]
    public async Task NullTierRates_AreSkipped_NotValidated()
    {
        // INFINITE com Rates=null deve ser silenciosamente pulado, não throws.
        var opts = new TierPricingOptions
        {
            Rates = new Dictionary<SellerTier, TierFees?>
            {
                [SellerTier.SILVER] = TierWithPix(pixPercent: 3m, pixFixed: 0.50m), // OK
                [SellerTier.INFINITE] = null, // pula
            }
        };
        _costSvc.CalculateProviderCostAsync(PaymentProvider.OPENPIX, PaymentType.PIX, Arg.Any<decimal>())
            .Returns(0.80m);
        _costSvc.CalculateProviderCostAsync(PaymentProvider.STRIPE, Arg.Any<PaymentType>(), Arg.Any<decimal>())
            .Returns(0.50m);

        await BuildValidator(opts).StartAsync(CancellationToken.None);
    }
}
