using FluentAssertions;
using FellowCore.Application.Modules.Sellers.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using NSubstitute;
// Usa o FakeTimeProvider definido no fim de AdvanceRiskEvaluatorTests.cs
// (internal sealed no mesmo namespace FellowCore.Application.Tests.Services).

namespace FellowCore.Application.Tests.Services;

/// <summary>
/// Cobre só a função pura <c>Resolve</c> — o método público <c>GetTierAsync</c>
/// é só DI wiring + delegação pra Resolve, não vale o esforço de mockar repo
/// + TimeProvider pra ele na Sprint 0.
///
/// MemberData em vez de InlineData porque xUnit não aceita literal decimal
/// em [InlineData] — silenciosamente converte de double e perde precisão
/// (49_999.99 vira 49999.9899999... e quebra o cast pra decimal).
/// </summary>
public class SellerTierServiceTests
{
    public static IEnumerable<object?[]> ResolveCases() => new[]
    {
        new object?[] { 0m,            SellerTier.SILVER,   (SellerTier?)SellerTier.GOLD,     (decimal?)50_000m   },
        new object?[] { 49_999.99m,    SellerTier.SILVER,   (SellerTier?)SellerTier.GOLD,     (decimal?)0.01m     },
        new object?[] { 50_000m,       SellerTier.GOLD,     (SellerTier?)SellerTier.DIAMOND, (decimal?)200_000m  },
        new object?[] { 249_999.99m,   SellerTier.GOLD,     (SellerTier?)SellerTier.DIAMOND, (decimal?)0.01m     },
        new object?[] { 250_000m,      SellerTier.DIAMOND, (SellerTier?)SellerTier.BLACK,    (decimal?)750_000m  },
        new object?[] { 999_999.99m,   SellerTier.DIAMOND, (SellerTier?)SellerTier.BLACK,    (decimal?)0.01m     },
        new object?[] { 1_000_000m,    SellerTier.BLACK,    (SellerTier?)null,                (decimal?)null      },
        new object?[] { 50_000_000m,   SellerTier.BLACK,    (SellerTier?)null,                (decimal?)null      },
    };

    [Theory]
    [MemberData(nameof(ResolveCases))]
    public void Resolve_MapsTpvToCorrectTier(
        decimal tpv,
        SellerTier expectedTier,
        SellerTier? expectedNext,
        decimal? expectedGap)
    {
        var (tier, next, gap) = SellerTierService.Resolve(tpv);
        tier.Should().Be(expectedTier);
        next.Should().Be(expectedNext);
        if (expectedGap is null) gap.Should().BeNull();
        else gap.Should().BeApproximately(expectedGap.Value, 0.001m);
    }

    [Fact]
    public void Resolve_NeverReturnsInfinite_AsAutoTier()
    {
        // Confirma o invariante de design — INFINITE é só via admin override.
        // Qualquer TPV (incluindo o maior representável em decimal) deve resolver pra BLACK no máximo.
        var result = SellerTierService.Resolve(decimal.MaxValue / 2);
        result.CurrentTier.Should().Be(SellerTier.BLACK);
    }

    // --- GetTierAsync: profile persistido tem precedência sobre on-the-fly ---

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static (SellerTierService svc, ISellerRepository sellerRepo, ISellerTierProfileRepository profileRepo)
        BuildSvc(Seller seller, SellerTierProfile? profile, decimal tpv30dFallback)
    {
        var sellerRepo = Substitute.For<ISellerRepository>();
        sellerRepo.GetByIdAsync(TenantId, SellerId).Returns(seller);
        sellerRepo.GetCapturedNetSumAsync(TenantId, SellerId, Arg.Any<DateTime>()).Returns(tpv30dFallback);

        var profileRepo = Substitute.For<ISellerTierProfileRepository>();
        profileRepo.GetBySellerIdAsync(TenantId, SellerId).Returns(profile);

        var time = new FakeTimeProvider(Now);
        var svc = new SellerTierService(sellerRepo, profileRepo, time);
        return (svc, sellerRepo, profileRepo);
    }

    private static Seller BuildSeller() => SellerTestFactory.NewSeller(TenantId, SellerId);

    [Fact]
    public async Task GetTierAsync_NoProfile_FallsBackToOnTheFly_FromTpv30d()
    {
        var (svc, sellerRepo, profileRepo) = BuildSvc(BuildSeller(), profile: null, tpv30dFallback: 60_000m);

        var dto = await svc.GetTierAsync(TenantId, SellerId);

        dto.CurrentTier.Should().Be(SellerTier.GOLD);  // 60k → GOLD (50k-250k)
        dto.Tpv30dBrl.Should().Be(60_000m);
        await profileRepo.Received(1).GetBySellerIdAsync(TenantId, SellerId);
        await sellerRepo.Received(1).GetCapturedNetSumAsync(TenantId, SellerId, Arg.Any<DateTime>());
    }

    [Fact]
    public async Task GetTierAsync_WithProfile_UsesProfile_SkipsOnTheFlyQuery()
    {
        var profile = SellerTierProfile.Create(TenantId, SellerId, SellerTier.DIAMOND, 300_000m, Now.AddDays(-5));
        var (svc, sellerRepo, _) = BuildSvc(BuildSeller(), profile, tpv30dFallback: 999m);

        var dto = await svc.GetTierAsync(TenantId, SellerId);

        dto.CurrentTier.Should().Be(SellerTier.DIAMOND);
        dto.Tpv30dBrl.Should().Be(300_000m);  // snapshot do profile (TPV90d), não o fallback 999
        dto.NextTier.Should().Be(SellerTier.BLACK);
        dto.GapToNextBrl.Should().Be(700_000m);
        // Fallback não foi consultado
        await sellerRepo.DidNotReceive().GetCapturedNetSumAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task GetTierAsync_ProfileAtBlack_ReturnsNoNextTier()
    {
        var profile = SellerTierProfile.Create(TenantId, SellerId, SellerTier.BLACK, 5_000_000m, Now);
        var (svc, _, _) = BuildSvc(BuildSeller(), profile, tpv30dFallback: 0m);

        var dto = await svc.GetTierAsync(TenantId, SellerId);

        dto.CurrentTier.Should().Be(SellerTier.BLACK);
        dto.NextTier.Should().BeNull();
        dto.GapToNextBrl.Should().BeNull();
    }

    [Fact]
    public async Task GetTierAsync_IncludesFoundingFromSeller_EvenWhenProfileExists()
    {
        var seller = SellerTestFactory.NewSeller(TenantId, SellerId);
        seller.SetFounding(7);
        var profile = SellerTierProfile.Create(TenantId, SellerId, SellerTier.SILVER, 1_000m, Now);
        var (svc, _, _) = BuildSvc(seller, profile, tpv30dFallback: 0m);

        var dto = await svc.GetTierAsync(TenantId, SellerId);

        dto.IsFoundingSeller.Should().BeTrue();
        dto.FoundingNumber.Should().Be(7);
        dto.CurrentTier.Should().Be(SellerTier.SILVER);  // founding é ortogonal a tier
    }
}

/// <summary>Helper pra criar Sellers minimamente válidos pros testes.</summary>
internal static class SellerTestFactory
{
    public static Seller NewSeller(Guid tenantId, Guid sellerId)
    {
        var s = Seller.Create(
            tenantId: tenantId,
            legalName: "Test Seller",
            document: "12345678000190",
            email: $"seller-{sellerId:N}@test.local",
            webhookSecret: "wh_secret_test");
        // Id é gerado pelo Create — pra testes precisamos forçar pra match o Substitute.
        typeof(Seller).BaseType!.GetProperty("Id")!.SetValue(s, sellerId);
        return s;
    }
}
