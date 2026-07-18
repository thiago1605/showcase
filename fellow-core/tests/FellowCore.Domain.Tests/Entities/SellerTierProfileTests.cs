using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class SellerTierProfileTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Seller = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SellerTierProfile NewSilver(decimal tpv = 0m) =>
        SellerTierProfile.Create(Tenant, Seller, SellerTier.SILVER, tpv, T0);

    [Fact]
    public void Create_StartsAtInitialTier_NoTransitionsYet()
    {
        var p = NewSilver();
        p.Tier.Should().Be(SellerTier.SILVER);
        p.UpgradedAt.Should().BeNull();
        p.DowngradedAt.Should().BeNull();
        p.FrozenUntil.Should().BeNull();
        p.IsFrozen(T0.AddYears(1)).Should().BeFalse();
    }

    [Fact]
    public void Apply_SameTier_ReturnsUnchanged_RefreshesSnapshot()
    {
        var p = NewSilver(tpv: 1_000m);
        var t1 = T0.AddDays(30);

        var transition = p.Apply(SellerTier.SILVER, 5_000m, t1);

        transition.Should().Be(SellerTierTransition.Unchanged);
        p.Tier.Should().Be(SellerTier.SILVER);
        p.Tpv90dSnapshotBrl.Should().Be(5_000m);
        p.ComputedAt.Should().Be(t1);
        p.UpgradedAt.Should().BeNull();
        p.DowngradedAt.Should().BeNull();
    }

    [Fact]
    public void Apply_HigherTier_ReturnsUpgraded_StampsUpgradedAt()
    {
        var p = NewSilver();
        var t1 = T0.AddDays(45);

        var transition = p.Apply(SellerTier.GOLD, 60_000m, t1);

        transition.Should().Be(SellerTierTransition.Upgraded);
        p.Tier.Should().Be(SellerTier.GOLD);
        p.UpgradedAt.Should().Be(t1);
        p.DowngradedAt.Should().BeNull();
    }

    [Fact]
    public void Apply_LowerTier_ReturnsDowngraded_StampsDowngradedAt()
    {
        var p = NewSilver();
        p.Apply(SellerTier.GOLD, 60_000m, T0.AddDays(30));
        var t2 = T0.AddDays(120);

        var transition = p.Apply(SellerTier.SILVER, 10_000m, t2);

        transition.Should().Be(SellerTierTransition.Downgraded);
        p.Tier.Should().Be(SellerTier.SILVER);
        p.DowngradedAt.Should().Be(t2);
        p.UpgradedAt.Should().Be(T0.AddDays(30)); // não foi sobrescrito
    }

    [Fact]
    public void Apply_UpgradeWhileFrozen_IsBlocked_TierStaysSame()
    {
        var p = NewSilver();
        p.Freeze(T0.AddDays(60), "chargeback_rate=2.4%", T0);
        var t1 = T0.AddDays(30);

        var transition = p.Apply(SellerTier.GOLD, 60_000m, t1);

        transition.Should().Be(SellerTierTransition.BlockedByFreeze);
        p.Tier.Should().Be(SellerTier.SILVER); // não subiu
        p.UpgradedAt.Should().BeNull();
        p.Tpv90dSnapshotBrl.Should().Be(60_000m); // snapshot atualiza mesmo bloqueado
    }

    [Fact]
    public void Apply_DowngradeWhileFrozen_IsAllowed_TierGoesDown()
    {
        var p = NewSilver();
        p.Apply(SellerTier.GOLD, 60_000m, T0.AddDays(30));
        p.Freeze(T0.AddDays(120), "chargeback_rate=3%", T0.AddDays(60));
        var t2 = T0.AddDays(90);

        // Mesmo congelado, descida passa — proteger a plataforma > respeitar freeze.
        var transition = p.Apply(SellerTier.SILVER, 5_000m, t2);

        transition.Should().Be(SellerTierTransition.Downgraded);
        p.Tier.Should().Be(SellerTier.SILVER);
        p.IsFrozen(t2).Should().BeTrue(); // continua congelado pra evitar re-subida
    }

    [Fact]
    public void Apply_AfterFreezeExpires_UpgradeWorks()
    {
        var p = NewSilver();
        p.Freeze(T0.AddDays(30), "chargeback", T0);
        var afterFreeze = T0.AddDays(31);

        var transition = p.Apply(SellerTier.GOLD, 60_000m, afterFreeze);

        transition.Should().Be(SellerTierTransition.Upgraded);
        p.Tier.Should().Be(SellerTier.GOLD);
    }

    [Fact]
    public void Freeze_WithEmptyReason_Throws() =>
        FluentActions.Invoking(() => NewSilver().Freeze(T0.AddDays(30), "", T0))
            .Should().Throw<ArgumentException>().WithParameterName("reason");

    [Fact]
    public void Freeze_WithPastDate_Throws() =>
        FluentActions.Invoking(() => NewSilver().Freeze(T0.AddDays(-1), "rs", T0))
            .Should().Throw<ArgumentException>().WithParameterName("until");

    [Fact]
    public void Unfreeze_ClearsBothFields()
    {
        var p = NewSilver();
        p.Freeze(T0.AddDays(60), "rs", T0);
        p.Unfreeze(T0.AddDays(1));

        p.FrozenUntil.Should().BeNull();
        p.FreezeReason.Should().BeNull();
        p.IsFrozen(T0.AddDays(1)).Should().BeFalse();
    }

    [Fact]
    public void IsFrozen_AtExactExpiry_ReturnsFalse()
    {
        var p = NewSilver();
        p.Freeze(T0.AddDays(30), "rs", T0);

        p.IsFrozen(T0.AddDays(30)).Should().BeFalse(); // strict > comparison
        p.IsFrozen(T0.AddDays(30).AddTicks(-1)).Should().BeTrue();
    }
}
