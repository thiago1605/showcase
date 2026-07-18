using FluentAssertions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Tests.Services; // FakeTimeProvider (internal sealed)
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Events;
using FellowCore.Domain.Interfaces;
using FellowCore.Domain.Primitives;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FellowCore.Application.Tests.Processors;

public class SellerTierRecomputeProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 6, 1, 4, 0, 0, DateTimeKind.Utc);

    private readonly ISellerRepository _sellerRepo = Substitute.For<ISellerRepository>();
    private readonly ISellerTierProfileRepository _profileRepo = Substitute.For<ISellerTierProfileRepository>();
    private readonly ISellerRiskProfileRepository _riskRepo = Substitute.For<ISellerRiskProfileRepository>();
    private readonly IDomainEventDispatcher _dispatcher = Substitute.For<IDomainEventDispatcher>();

    private SellerTierRecomputeProcessor BuildProc() => new(
        _sellerRepo, _profileRepo, _riskRepo, _dispatcher,
        new FakeTimeProvider(Now),
        NullLogger<SellerTierRecomputeProcessor>.Instance);

    private void SetupSellerWithTpv(decimal tpv90d, decimal tpv30d)
    {
        _sellerRepo.GetActiveTenantSellerPairsAsync(Arg.Any<int>())
            .Returns(new List<(Guid, Guid)> { (TenantId, SellerId) });
        _sellerRepo.GetCapturedNetSumAsync(TenantId, SellerId, Arg.Is<DateTime>(d => d < Now.AddDays(-60)))
            .Returns(tpv90d);
        _sellerRepo.GetCapturedNetSumAsync(TenantId, SellerId, Arg.Is<DateTime>(d => d >= Now.AddDays(-60)))
            .Returns(tpv30d);
    }

    [Fact]
    public async Task NewSeller_NoProfile_CreatesAtTierFromTpv90d()
    {
        SetupSellerWithTpv(tpv90d: 60_000m, tpv30d: 60_000m);
        _profileRepo.GetBySellerIdAsync(TenantId, SellerId).Returns((SellerTierProfile?)null);

        await BuildProc().ProcessAsync();

        _profileRepo.Received(1).Add(Arg.Is<SellerTierProfile>(p =>
            p.Tier == SellerTier.GOLD &&
            p.Tpv90dSnapshotBrl == 60_000m &&
            p.UpgradedAt == null));  // primeira atribuição não conta como upgrade
        await _profileRepo.Received(1).SaveChangesAsync();
        await _dispatcher.DidNotReceive().DispatchAsync(Arg.Any<IReadOnlyList<IDomainEvent>>());
    }

    [Fact]
    public async Task ExistingSilver_Tpv90dGold_AndTpv30dGold_Upgrades()
    {
        var profile = SellerTierProfile.Create(TenantId, SellerId, SellerTier.SILVER, 10_000m, Now.AddDays(-90));
        SetupSellerWithTpv(tpv90d: 60_000m, tpv30d: 60_000m);
        _profileRepo.GetBySellerIdAsync(TenantId, SellerId).Returns(profile);

        await BuildProc().ProcessAsync();

        profile.Tier.Should().Be(SellerTier.GOLD);
        profile.UpgradedAt.Should().Be(Now);
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<IReadOnlyList<IDomainEvent>>(evts =>
                evts.Count == 1 &&
                ((SellerTierChangedEvent)evts[0]).Transition == SellerTierTransition.Upgraded &&
                ((SellerTierChangedEvent)evts[0]).NewTier == SellerTier.GOLD));
    }

    [Fact]
    public async Task ExistingSilver_Tpv90dGold_ButTpv30dStillSilver_BlockedByConsistency()
    {
        // Spike no 90d (ex: Black Friday) mas mês atual baixo — não promove.
        var profile = SellerTierProfile.Create(TenantId, SellerId, SellerTier.SILVER, 10_000m, Now.AddDays(-90));
        SetupSellerWithTpv(tpv90d: 60_000m, tpv30d: 10_000m);
        _profileRepo.GetBySellerIdAsync(TenantId, SellerId).Returns(profile);

        await BuildProc().ProcessAsync();

        profile.Tier.Should().Be(SellerTier.SILVER);  // não subiu
        profile.UpgradedAt.Should().BeNull();
        profile.Tpv90dSnapshotBrl.Should().Be(60_000m); // snapshot atualizado
        await _dispatcher.DidNotReceive().DispatchAsync(Arg.Any<IReadOnlyList<IDomainEvent>>());
    }

    [Fact]
    public async Task ExistingGold_Tpv90dSilver_ButRecentlyUpgraded_BlockedByCooldown()
    {
        var profile = SellerTierProfile.Create(TenantId, SellerId, SellerTier.SILVER, 10_000m, Now.AddDays(-100));
        profile.Apply(SellerTier.GOLD, 60_000m, Now.AddDays(-30));  // promovido há 30d
        SetupSellerWithTpv(tpv90d: 10_000m, tpv30d: 10_000m);  // TPV caiu
        _profileRepo.GetBySellerIdAsync(TenantId, SellerId).Returns(profile);

        await BuildProc().ProcessAsync();

        profile.Tier.Should().Be(SellerTier.GOLD);  // não rebaixou (cooldown 60d)
        await _dispatcher.DidNotReceive().DispatchAsync(Arg.Any<IReadOnlyList<IDomainEvent>>());
    }

    [Fact]
    public async Task ExistingGold_Tpv90dSilver_UpgradedLongAgo_Downgrades()
    {
        var profile = SellerTierProfile.Create(TenantId, SellerId, SellerTier.SILVER, 10_000m, Now.AddDays(-200));
        profile.Apply(SellerTier.GOLD, 60_000m, Now.AddDays(-90));  // promovido há 90d, fora cooldown
        SetupSellerWithTpv(tpv90d: 10_000m, tpv30d: 10_000m);
        _profileRepo.GetBySellerIdAsync(TenantId, SellerId).Returns(profile);

        await BuildProc().ProcessAsync();

        profile.Tier.Should().Be(SellerTier.SILVER);
        profile.DowngradedAt.Should().Be(Now);
        await _dispatcher.Received(1).DispatchAsync(
            Arg.Is<IReadOnlyList<IDomainEvent>>(evts =>
                ((SellerTierChangedEvent)evts[0]).Transition == SellerTierTransition.Downgraded));
    }

    [Fact]
    public async Task ChargebackOver2Pct_Freezes_AndBlocksUpgrade()
    {
        var profile = SellerTierProfile.Create(TenantId, SellerId, SellerTier.SILVER, 10_000m, Now.AddDays(-90));
        SetupSellerWithTpv(tpv90d: 60_000m, tpv30d: 60_000m);  // mereceria GOLD
        _profileRepo.GetBySellerIdAsync(TenantId, SellerId).Returns(profile);

        var risk = SellerRiskProfile.CreateOrUpdate(SellerId,
            capturedCount: 100, lostCount: 3, capturedVolume: 10_000m, Now);
        // 3/100 = 3% > threshold 2%
        _riskRepo.GetBySellerIdAsync(SellerId).Returns(risk);

        await BuildProc().ProcessAsync();

        profile.IsFrozen(Now).Should().BeTrue();
        profile.FreezeReason.Should().StartWith("chargeback_rate=");
        profile.Tier.Should().Be(SellerTier.SILVER);  // upgrade bloqueado pelo freeze
        await _dispatcher.DidNotReceive().DispatchAsync(Arg.Any<IReadOnlyList<IDomainEvent>>());
    }

    [Fact]
    public async Task ChargebackBelow2Pct_AndProfileWasFrozenForChargeback_Unfreezes()
    {
        var profile = SellerTierProfile.Create(TenantId, SellerId, SellerTier.SILVER, 10_000m, Now.AddDays(-90));
        profile.Freeze(Now.AddDays(30), "chargeback_rate=3.00% > 2% (sample=100)", Now.AddDays(-30));
        SetupSellerWithTpv(tpv90d: 10_000m, tpv30d: 10_000m);
        _profileRepo.GetBySellerIdAsync(TenantId, SellerId).Returns(profile);

        var risk = SellerRiskProfile.CreateOrUpdate(SellerId,
            capturedCount: 200, lostCount: 1, capturedVolume: 20_000m, Now);
        // 1/200 = 0.5% < threshold
        _riskRepo.GetBySellerIdAsync(SellerId).Returns(risk);

        await BuildProc().ProcessAsync();

        profile.IsFrozen(Now).Should().BeFalse();
        profile.FreezeReason.Should().BeNull();
    }

    [Fact]
    public async Task NoRiskProfile_DoesNotCrashAndDoesNotChangeFreeze()
    {
        var profile = SellerTierProfile.Create(TenantId, SellerId, SellerTier.SILVER, 10_000m, Now.AddDays(-90));
        SetupSellerWithTpv(tpv90d: 10_000m, tpv30d: 10_000m);
        _profileRepo.GetBySellerIdAsync(TenantId, SellerId).Returns(profile);
        _riskRepo.GetBySellerIdAsync(SellerId).Returns((SellerRiskProfile?)null);

        await BuildProc().ProcessAsync();

        profile.IsFrozen(Now).Should().BeFalse();
        profile.Tier.Should().Be(SellerTier.SILVER);
    }

    [Fact]
    public async Task SellerThrows_OtherSellersStillProcessed()
    {
        var otherSellerId = Guid.NewGuid();
        _sellerRepo.GetActiveTenantSellerPairsAsync(Arg.Any<int>())
            .Returns(new List<(Guid, Guid)> { (TenantId, SellerId), (TenantId, otherSellerId) });

        // Primeiro seller: throw na query de TPV
        _sellerRepo.GetCapturedNetSumAsync(TenantId, SellerId, Arg.Any<DateTime>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("simulated DB failure"));

        // Segundo seller: tudo OK
        _sellerRepo.GetCapturedNetSumAsync(TenantId, otherSellerId, Arg.Any<DateTime>())
            .Returns(60_000m);
        _profileRepo.GetBySellerIdAsync(TenantId, otherSellerId).Returns((SellerTierProfile?)null);

        await BuildProc().ProcessAsync();  // não throws

        // Segundo seller deve ter sido criado mesmo após erro no primeiro
        _profileRepo.Received(1).Add(Arg.Is<SellerTierProfile>(p => p.SellerId == otherSellerId));
    }
}
