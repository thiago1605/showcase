using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

/// <summary>
/// Cobre invariantes do Modelo Híbrido — reserva de caixa do tenant + limite per-seller.
/// Garantias críticas:
///   - Reserve nunca fica negativa (DebitAdvanceReserve falha)
///   - Seller exposure não passa do AdvanceCreditLimit (IncreaseAdvanceExposure falha)
///   - DecreaseAdvanceExposure clampa em 0 (não permite negativo)
/// </summary>
public class AdvanceReserveTests
{
    private static TenantConfig BuildConfig()
    {
        var tc = TenantConfig.Create(Guid.NewGuid());
        return tc;
    }

    private static Seller BuildSeller(decimal advanceLimit = 0m)
    {
        var seller = Seller.Create(
            tenantId: Guid.NewGuid(), legalName: "X", document: "1", email: "x@x.com",
            webhookSecret: "ws", preferredProvider: PaymentProvider.STRIPE,
            externalAccountId: "acct_x", encryptedAccessToken: "enc");
        if (advanceLimit > 0)
            seller.SetAdvanceCreditLimit(advanceLimit);
        return seller;
    }

    // --- TenantConfig.AdvanceReserve ---

    [Fact]
    public void HasAdvanceReserveFor_BalancePositive_ReturnsTrue()
    {
        var tc = BuildConfig();
        tc.TopUpAdvanceReserve(100_000); // R$ 1.000

        tc.HasAdvanceReserveFor(50_000).Should().BeTrue();
    }

    [Fact]
    public void HasAdvanceReserveFor_InsufficientBalance_ReturnsFalse()
    {
        var tc = BuildConfig();
        tc.TopUpAdvanceReserve(100_000);

        tc.HasAdvanceReserveFor(150_000).Should().BeFalse();
    }

    [Fact]
    public void HasAdvanceReserveFor_ZeroAmount_ReturnsFalse()
    {
        var tc = BuildConfig();
        tc.TopUpAdvanceReserve(100_000);

        tc.HasAdvanceReserveFor(0).Should().BeFalse("zero não é uma requisição válida");
    }

    [Fact]
    public void DebitAdvanceReserve_Sufficient_DecrementsBalance()
    {
        var tc = BuildConfig();
        tc.TopUpAdvanceReserve(100_000);

        var result = tc.DebitAdvanceReserve(30_000);

        result.IsSuccess.Should().BeTrue();
        tc.PlatformAdvanceReserveCents.Should().Be(70_000);
    }

    [Fact]
    public void DebitAdvanceReserve_Insufficient_Fails_NoChange()
    {
        var tc = BuildConfig();
        tc.TopUpAdvanceReserve(50_000);

        var result = tc.DebitAdvanceReserve(100_000);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("TenantConfig.InsufficientAdvanceReserve");
        tc.PlatformAdvanceReserveCents.Should().Be(50_000, "reserve não deve mudar em failure");
    }

    [Fact]
    public void DebitAdvanceReserve_NegativeOrZero_Fails()
    {
        var tc = BuildConfig();
        tc.TopUpAdvanceReserve(100_000);

        tc.DebitAdvanceReserve(0).IsFailure.Should().BeTrue();
        tc.DebitAdvanceReserve(-100).IsFailure.Should().BeTrue();
        tc.PlatformAdvanceReserveCents.Should().Be(100_000);
    }

    [Fact]
    public void CreditAdvanceReserve_Idempotente_E_Acumulativo()
    {
        var tc = BuildConfig();
        tc.CreditAdvanceReserve(50_000);
        tc.CreditAdvanceReserve(30_000);

        tc.PlatformAdvanceReserveCents.Should().Be(80_000);
    }

    // --- Seller.AdvanceCreditLimit + Exposure ---

    [Fact]
    public void SetAdvanceCreditLimit_Negative_Fails()
    {
        var seller = BuildSeller();

        var result = seller.SetAdvanceCreditLimit(-1m);

        result.IsFailure.Should().BeTrue();
        seller.AdvanceCreditLimit.Should().Be(0m);
    }

    [Fact]
    public void IncreaseAdvanceExposure_WithinLimit_Succeeds()
    {
        var seller = BuildSeller(advanceLimit: 10_000m);

        seller.IncreaseAdvanceExposure(3_000m).IsSuccess.Should().BeTrue();
        seller.AdvanceExposureCurrent.Should().Be(3_000m);

        seller.IncreaseAdvanceExposure(5_000m).IsSuccess.Should().BeTrue();
        seller.AdvanceExposureCurrent.Should().Be(8_000m);
    }

    [Fact]
    public void IncreaseAdvanceExposure_ExceedsLimit_Fails()
    {
        var seller = BuildSeller(advanceLimit: 5_000m);
        seller.IncreaseAdvanceExposure(4_000m);

        var result = seller.IncreaseAdvanceExposure(2_000m); // 4k + 2k > 5k

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Seller.AdvanceLimitReached");
        seller.AdvanceExposureCurrent.Should().Be(4_000m, "estado não muda em failure");
    }

    [Fact]
    public void CanIncreaseAdvanceExposure_Boundary()
    {
        var seller = BuildSeller(advanceLimit: 5_000m);
        seller.IncreaseAdvanceExposure(4_000m);

        seller.CanIncreaseAdvanceExposure(1_000m).Should().BeTrue("exato no limite");
        seller.CanIncreaseAdvanceExposure(1_001m).Should().BeFalse("1 centavo acima");
        seller.CanIncreaseAdvanceExposure(0m).Should().BeFalse("zero não é válido");
    }

    [Fact]
    public void DecreaseAdvanceExposure_ClampsAtZero()
    {
        var seller = BuildSeller(advanceLimit: 5_000m);
        seller.IncreaseAdvanceExposure(3_000m);

        // Reconciler tenta debitar mais do que o tracked — clampa em 0 sem falhar
        seller.DecreaseAdvanceExposure(5_000m).IsSuccess.Should().BeTrue();
        seller.AdvanceExposureCurrent.Should().Be(0m);
    }

    [Fact]
    public void Seller_DefaultLimit_IsZero_BlockingAll()
    {
        var seller = BuildSeller();

        seller.AdvanceCreditLimit.Should().Be(0m);
        seller.CanIncreaseAdvanceExposure(1m).Should().BeFalse(
            "default 0 = não antecipa pra este seller até admin setar limit");
    }
}
