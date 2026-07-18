using FluentAssertions;
using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Application.Modules.Splits.Services;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Tests.Services;

public class SplitCalculationServiceTests
{
    private readonly SplitCalculationService _sut = new();

    // ── Basic: percentage-only splits ─────────────────────────────────────

    [Fact]
    public void Calculate_TwoPercentageRecipients_ShouldDistributeProportionally()
    {
        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();

        var input = new SplitCalculationInput(
            GrossAmount: 100m,
            NetAmount: 98.50m,
            PlatformFee: 1.50m,
            ProviderCost: 0.80m,
            Recipients:
            [
                new SplitRecipientInput(seller1, null, 60m, 0),
                new SplitRecipientInput(seller2, null, 40m, 1)
            ],
            FeePolicy: FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES
        );

        var result = _sut.Calculate(input);

        result.Recipients.Should().HaveCount(2);
        result.PrimarySellerId.Should().Be(seller1);

        var r1 = result.Recipients.First(r => r.SellerId == seller1);
        var r2 = result.Recipients.First(r => r.SellerId == seller2);

        // Gross shares: 60% of 98.50 = 59.10, 40% of 98.50 = 39.40
        r1.GrossShare.Should().Be(59.10m);
        r2.GrossShare.Should().Be(39.40m);

        // PRIMARY_SELLER_PAYS_FEES: seller1 pays all fees
        r1.FeeShare.Should().Be(1.50m);
        r2.FeeShare.Should().Be(0m);

        // Net: seller1 = 59.10 - 1.50 = 57.60, seller2 = 39.40
        r1.NetShare.Should().Be(57.60m);
        r2.NetShare.Should().Be(39.40m);
    }

    // ── Basic: fixed-only splits ──────────────────────────────────────────

    [Fact]
    public void Calculate_TwoFixedRecipients_ShouldAllocateByPriority()
    {
        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();

        var input = new SplitCalculationInput(
            GrossAmount: 100m,
            NetAmount: 97m,
            PlatformFee: 3m,
            ProviderCost: 1m,
            Recipients:
            [
                new SplitRecipientInput(seller1, 50m, null, 0),
                new SplitRecipientInput(seller2, 30m, null, 1)
            ],
            FeePolicy: FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES
        );

        var result = _sut.Calculate(input);

        result.Recipients.Should().HaveCount(2);

        var r1 = result.Recipients.First(r => r.SellerId == seller1);
        var r2 = result.Recipients.First(r => r.SellerId == seller2);

        r1.GrossShare.Should().Be(50m);
        r2.GrossShare.Should().Be(30m);

        // Primary seller pays all fees
        r1.NetShare.Should().Be(50m - 3m);
        r2.NetShare.Should().Be(30m);
    }

    // ── Mixed: fixed + percentage with priority ───────────────────────────

    [Fact]
    public void Calculate_FixedPlusPercentage_ShouldAllocateFixedFirstThenPercentOnRemainder()
    {
        var sellerFixed = Guid.NewGuid();
        var sellerPercent = Guid.NewGuid();

        var input = new SplitCalculationInput(
            GrossAmount: 200m,
            NetAmount: 194m,
            PlatformFee: 6m,
            ProviderCost: 2m,
            Recipients:
            [
                new SplitRecipientInput(sellerFixed, 50m, null, 0),     // Fixed R$50, priority 0
                new SplitRecipientInput(sellerPercent, null, 50m, 1)    // 50% of remainder, priority 1
            ],
            FeePolicy: FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES
        );

        var result = _sut.Calculate(input);

        var rFixed = result.Recipients.First(r => r.SellerId == sellerFixed);
        var rPercent = result.Recipients.First(r => r.SellerId == sellerPercent);

        // Fixed: 50 from 194 net
        rFixed.GrossShare.Should().Be(50m);
        // Percent: 50% of (194 - 50) = 50% of 144 = 72
        rPercent.GrossShare.Should().Be(72m);

        // Primary (sellerFixed, priority 0) pays all fees
        rFixed.NetShare.Should().Be(50m - 6m); // 44
        rPercent.NetShare.Should().Be(72m);
    }

    // ── FeeAllocationPolicy: PROPORTIONAL_TO_RECIPIENTS ──────────────────

    [Fact]
    public void Calculate_ProportionalFeePolicy_ShouldSplitFeesProportionally()
    {
        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();

        var input = new SplitCalculationInput(
            GrossAmount: 100m,
            NetAmount: 96m,
            PlatformFee: 4m,
            ProviderCost: 2m,
            Recipients:
            [
                new SplitRecipientInput(seller1, null, 75m, 0),
                new SplitRecipientInput(seller2, null, 25m, 1)
            ],
            FeePolicy: FeeAllocationPolicy.PROPORTIONAL_TO_RECIPIENTS
        );

        var result = _sut.Calculate(input);

        var r1 = result.Recipients.First(r => r.SellerId == seller1);
        var r2 = result.Recipients.First(r => r.SellerId == seller2);

        // Gross: 75% of 96 = 72, 25% of 96 = 24
        r1.GrossShare.Should().Be(72m);
        r2.GrossShare.Should().Be(24m);

        // Fee proportional: seller1 = 4 * (72/96) = 3, seller2 = 4 * (24/96) = 1
        r1.FeeShare.Should().Be(3m);
        r2.FeeShare.Should().Be(1m);

        // Net: seller1 = 72 - 3 = 69, seller2 = 24 - 1 = 23
        r1.NetShare.Should().Be(69m);
        r2.NetShare.Should().Be(23m);
    }

    // ── FeeAllocationPolicy: PLATFORM_ABSORBS ────────────────────────────

    [Fact]
    public void Calculate_PlatformAbsorbs_ShouldNotDeductFeesFromRecipients()
    {
        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();

        var input = new SplitCalculationInput(
            GrossAmount: 100m,
            NetAmount: 95m,
            PlatformFee: 5m,
            ProviderCost: 3m,
            Recipients:
            [
                new SplitRecipientInput(seller1, null, 60m, 0),
                new SplitRecipientInput(seller2, null, 40m, 1)
            ],
            FeePolicy: FeeAllocationPolicy.PLATFORM_ABSORBS
        );

        var result = _sut.Calculate(input);

        var r1 = result.Recipients.First(r => r.SellerId == seller1);
        var r2 = result.Recipients.First(r => r.SellerId == seller2);

        // Gross = Net because platform absorbs fees
        r1.FeeShare.Should().Be(0m);
        r2.FeeShare.Should().Be(0m);
        r1.NetShare.Should().Be(r1.GrossShare);
        r2.NetShare.Should().Be(r2.GrossShare);
    }

    // ── Edge case: fixed exceeds available amount ─────────────────────────

    [Fact]
    public void Calculate_FixedExceedsNet_ShouldCapAtAvailableAmount()
    {
        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();

        var input = new SplitCalculationInput(
            GrossAmount: 50m,
            NetAmount: 48m,
            PlatformFee: 2m,
            ProviderCost: 1m,
            Recipients:
            [
                new SplitRecipientInput(seller1, 40m, null, 0),  // priority 0
                new SplitRecipientInput(seller2, 30m, null, 1)   // priority 1 — only 8 left
            ],
            FeePolicy: FeeAllocationPolicy.PLATFORM_ABSORBS
        );

        var result = _sut.Calculate(input);

        var r1 = result.Recipients.First(r => r.SellerId == seller1);
        var r2 = result.Recipients.First(r => r.SellerId == seller2);

        // seller1 gets full 40, seller2 capped at remaining 8
        r1.GrossShare.Should().Be(40m);
        r2.GrossShare.Should().Be(8m);
    }

    // ── Rounding adjustment ──────────────────────────────────────────────

    [Fact]
    public void Calculate_ThreeRecipients_RoundingAdjustmentGoesToPrimary()
    {
        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();
        var seller3 = Guid.NewGuid();

        // 100.00 split 3 ways (33.33..%) — forces rounding
        var input = new SplitCalculationInput(
            GrossAmount: 100m,
            NetAmount: 100m,
            PlatformFee: 0m,
            ProviderCost: 0m,
            Recipients:
            [
                new SplitRecipientInput(seller1, null, 33.33m, 0),
                new SplitRecipientInput(seller2, null, 33.33m, 1),
                new SplitRecipientInput(seller3, null, 33.34m, 2)
            ],
            FeePolicy: FeeAllocationPolicy.PLATFORM_ABSORBS
        );

        var result = _sut.Calculate(input);

        // Sum of net shares should equal net amount (with rounding adjustment)
        decimal totalNet = result.Recipients.Sum(r => r.NetShare);
        totalNet.Should().Be(100m);
    }

    // ── Empty recipients ─────────────────────────────────────────────────

    [Fact]
    public void Calculate_NoRecipients_ShouldReturnEmpty()
    {
        var input = new SplitCalculationInput(
            GrossAmount: 100m,
            NetAmount: 98m,
            PlatformFee: 2m,
            ProviderCost: 1m,
            Recipients: [],
            FeePolicy: FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES
        );

        var result = _sut.Calculate(input);

        result.Recipients.Should().BeEmpty();
        result.RoundingAdjustment.Should().Be(0m);
    }

    // ── Single recipient with PRIMARY_SELLER_PAYS_FEES ───────────────────

    [Fact]
    public void Calculate_SingleRecipient_ShouldPayAllFees()
    {
        var seller = Guid.NewGuid();

        var input = new SplitCalculationInput(
            GrossAmount: 100m,
            NetAmount: 97m,
            PlatformFee: 3m,
            ProviderCost: 1.50m,
            Recipients: [new SplitRecipientInput(seller, null, 100m, 0)],
            FeePolicy: FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES
        );

        var result = _sut.Calculate(input);

        result.Recipients.Should().HaveCount(1);
        var r = result.Recipients[0];
        r.GrossShare.Should().Be(97m);
        r.FeeShare.Should().Be(3m);
        r.NetShare.Should().Be(94m);
        r.ProviderCostShare.Should().Be(1.50m);
    }

    // ── Percentage that doesn't sum to 100% ──────────────────────────────

    [Fact]
    public void Calculate_PartialPercentage_ShouldAllocateOnlyRequestedAmount()
    {
        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();

        // Only 70% is allocated — platform keeps the rest
        var input = new SplitCalculationInput(
            GrossAmount: 100m,
            NetAmount: 100m,
            PlatformFee: 0m,
            ProviderCost: 0m,
            Recipients:
            [
                new SplitRecipientInput(seller1, null, 40m, 0),
                new SplitRecipientInput(seller2, null, 30m, 1)
            ],
            FeePolicy: FeeAllocationPolicy.PLATFORM_ABSORBS
        );

        var result = _sut.Calculate(input);

        var total = result.Recipients.Sum(r => r.NetShare);
        total.Should().Be(70m); // Only 70% allocated
    }

    // ── Provider cost share is tracked per recipient ──────────────────────

    [Fact]
    public void Calculate_ProportionalPolicy_ShouldTrackProviderCostPerRecipient()
    {
        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();

        var input = new SplitCalculationInput(
            GrossAmount: 100m,
            NetAmount: 96m,
            PlatformFee: 4m,
            ProviderCost: 2m,
            Recipients:
            [
                new SplitRecipientInput(seller1, 64m, null, 0),
                new SplitRecipientInput(seller2, 32m, null, 1)
            ],
            FeePolicy: FeeAllocationPolicy.PROPORTIONAL_TO_RECIPIENTS
        );

        var result = _sut.Calculate(input);

        var r1 = result.Recipients.First(r => r.SellerId == seller1);
        var r2 = result.Recipients.First(r => r.SellerId == seller2);

        // Ratio: 64/96 = 2/3, 32/96 = 1/3
        // Provider cost: seller1 = 2 * 2/3 = 1.33, seller2 = 2 * 1/3 = 0.67
        r1.ProviderCostShare.Should().Be(1.33m);
        r2.ProviderCostShare.Should().Be(0.67m);
    }
}
