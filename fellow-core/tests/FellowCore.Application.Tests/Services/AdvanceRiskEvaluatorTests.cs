using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Modules.Settlements.AdvanceRisk;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FellowCore.Application.Tests.Services;

/// <summary>
/// Cobre as 5 regras R1-R5 do anti-fraude de ADVANCE.
/// Cada teste isola UMA regra (fixando as outras como passantes) pra cobertura limpa.
/// </summary>
public class AdvanceRiskEvaluatorTests
{
    private readonly ITransactionRepository _txRepo = Substitute.For<ITransactionRepository>();
    private readonly ISellerRiskProfileRepository _profileRepo = Substitute.For<ISellerRiskProfileRepository>();
    private readonly FakeTimeProvider _time;
    private readonly AdvanceRiskEvaluator _sut;
    private readonly AdvanceRiskOptions _opt;

    public AdvanceRiskEvaluatorTests()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 14, 12, 0, 0, TimeSpan.Zero));
        _opt = new AdvanceRiskOptions(); // defaults
        var optWrapper = Substitute.For<IOptions<AdvanceRiskOptions>>();
        optWrapper.Value.Returns(_opt);

        // Default: profile null → falls back to on-demand stats
        _profileRepo.GetBySellerIdAsync(Arg.Any<Guid>()).Returns((FellowCore.Domain.Entities.SellerRiskProfile?)null);
        // Default: stats sem chargeback
        _txRepo.GetSellerRiskStatsAsync(Arg.Any<Guid>(), Arg.Any<DateTime>())
            .Returns(new SellerRiskStats(100, 0, 0m));

        _sut = new AdvanceRiskEvaluator(_txRepo, _profileRepo, optWrapper, _time,
            NullLogger<AdvanceRiskEvaluator>.Instance);
    }

    private Seller BuildEligibleSeller(int ageDays = 180)
    {
        var seller = Seller.Create(
            tenantId: Guid.NewGuid(), legalName: "Bruce", document: "12345678901",
            email: "b@x.com", webhookSecret: "ws",
            preferredProvider: PaymentProvider.STRIPE,
            externalAccountId: "acct_x",
            encryptedAccessToken: "enc");
        // backdate CreatedAt via reflection (private set)
        typeof(Seller).GetProperty("CreatedAt")!.SetValue(seller, _time.GetUtcNow().UtcDateTime.AddDays(-ageDays));
        return seller;
    }

    private Transaction BuildCreditTx(decimal amount = 100m, double? riskScore = null)
    {
        var tx = Transaction.Create(
            tenantId: Guid.NewGuid(), amount: amount, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 6, feeAmount: amount * 0.05m,
            netAmount: amount * 0.95m, expectedSettlementDate: null,
            providerTxId: "pi_x", sellerId: Guid.NewGuid()).Value;
        if (riskScore.HasValue)
            typeof(Transaction).GetProperty("RiskScore")!.SetValue(tx, riskScore.Value);
        return tx;
    }

    [Fact]
    public async Task R1_SellerSuspended_Blocks()
    {
        var seller = BuildEligibleSeller();
        seller.Suspend();

        var result = await _sut.EvaluateAsync(BuildCreditTx(), seller);

        result.IsEligible.Should().BeFalse();
        result.BlockReason.Should().Be("seller_not_active");
    }

    [Fact]
    public async Task R1_SellerNoConnectAccount_Blocks()
    {
        var seller = Seller.Create(
            tenantId: Guid.NewGuid(), legalName: "X", document: "9", email: "x@x.com",
            webhookSecret: "ws", preferredProvider: PaymentProvider.STRIPE,
            externalAccountId: null, // sem connect
            encryptedAccessToken: null);
        typeof(Seller).GetProperty("CreatedAt")!.SetValue(seller, _time.GetUtcNow().UtcDateTime.AddDays(-180));

        var result = await _sut.EvaluateAsync(BuildCreditTx(), seller);

        result.IsEligible.Should().BeFalse();
        result.BlockReason.Should().Be("seller_no_connect_account");
    }

    [Fact]
    public async Task R2_SellerTooNew_Blocks()
    {
        var seller = BuildEligibleSeller(ageDays: 10); // < 30 dias default

        var result = await _sut.EvaluateAsync(BuildCreditTx(), seller);

        result.IsEligible.Should().BeFalse();
        result.BlockReason.Should().Be("seller_too_new");
        result.Signals.Should().Contain(s => s.Contains("age_days=10"));
    }

    [Fact]
    public async Task R3_NewSellerHighValue_Blocks()
    {
        // Seller entre 30 e 90 dias com TX acima do cap (R$ 5k default) → block
        var seller = BuildEligibleSeller(ageDays: 60);
        var bigTx = BuildCreditTx(amount: 10_000m);

        var result = await _sut.EvaluateAsync(bigTx, seller);

        result.IsEligible.Should().BeFalse();
        result.BlockReason.Should().Be("amount_over_cap_for_new_seller");
    }

    [Fact]
    public async Task R3_EstablishedSellerHighValue_Allowed()
    {
        // Seller > 90 dias passa mesmo com valor alto
        var seller = BuildEligibleSeller(ageDays: 200);
        var bigTx = BuildCreditTx(amount: 10_000m);

        var result = await _sut.EvaluateAsync(bigTx, seller);

        result.IsEligible.Should().BeTrue();
    }

    [Fact]
    public async Task R4_HighRiskScore_Blocks()
    {
        var seller = BuildEligibleSeller();
        var riskyTx = BuildCreditTx(riskScore: 0.85);

        var result = await _sut.EvaluateAsync(riskyTx, seller);

        result.IsEligible.Should().BeFalse();
        result.BlockReason.Should().Be("high_risk_score");
    }

    [Fact]
    public async Task R4_BelowThresholdScore_Allowed()
    {
        var seller = BuildEligibleSeller();
        var okTx = BuildCreditTx(riskScore: 0.3);

        var result = await _sut.EvaluateAsync(okTx, seller);

        result.IsEligible.Should().BeTrue();
    }

    [Fact]
    public async Task R5_HighChargebackRate_Blocks()
    {
        var seller = BuildEligibleSeller();
        // 100 TXs, 5 chargebacks → 5% rate, acima do 1% default
        _txRepo.GetSellerRiskStatsAsync(Arg.Any<Guid>(), Arg.Any<DateTime>())
            .Returns(new SellerRiskStats(100, 5, 0.05m));

        var result = await _sut.EvaluateAsync(BuildCreditTx(), seller);

        result.IsEligible.Should().BeFalse();
        result.BlockReason.Should().Be("high_chargeback_rate");
        // Culture-agnostic: aceita rate=5.00% (en) ou rate=5,00% (pt-BR)
        result.Signals.Should().Contain(s => s.StartsWith("rate=5") && s.EndsWith("%"));
    }

    [Fact]
    public async Task R5_SmallSample_IgnoresRateEvenIfHigh()
    {
        // 5 TXs, 2 chargebacks = 40% rate MAS sample < threshold (10 default) → ignora
        var seller = BuildEligibleSeller();
        _txRepo.GetSellerRiskStatsAsync(Arg.Any<Guid>(), Arg.Any<DateTime>())
            .Returns(new SellerRiskStats(5, 2, 0.4m));

        var result = await _sut.EvaluateAsync(BuildCreditTx(), seller);

        result.IsEligible.Should().BeTrue("amostra pequena demais pra ser estatisticamente significativa");
    }

    [Fact]
    public async Task AllRulesPass_AllowsAdvance()
    {
        var seller = BuildEligibleSeller(ageDays: 180);
        var tx = BuildCreditTx(amount: 1_000m, riskScore: 0.2);
        _txRepo.GetSellerRiskStatsAsync(Arg.Any<Guid>(), Arg.Any<DateTime>())
            .Returns(new SellerRiskStats(500, 1, 0.002m)); // 0.2% rate, OK

        var result = await _sut.EvaluateAsync(tx, seller);

        result.IsEligible.Should().BeTrue();
        result.BlockReason.Should().BeNull();
    }

    [Fact]
    public async Task R5_UsesProfile_WhenAvailable_AndFresh()
    {
        // Profile materializado fresh → não chama on-demand stats
        var seller = BuildEligibleSeller();
        var freshProfile = FellowCore.Domain.Entities.SellerRiskProfile.CreateOrUpdate(
            seller.Id, capturedCount: 100, lostCount: 5, capturedVolume: 50_000m,
            now: _time.GetUtcNow().UtcDateTime); // fresh
        _profileRepo.GetBySellerIdAsync(seller.Id).Returns(freshProfile);

        var result = await _sut.EvaluateAsync(BuildCreditTx(), seller);

        result.IsEligible.Should().BeFalse("rate 5% > threshold 1%");
        result.BlockReason.Should().Be("high_chargeback_rate");
        // Confirma que NÃO foi pra fallback on-demand
        await _txRepo.DidNotReceive().GetSellerRiskStatsAsync(Arg.Any<Guid>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task R5_FallsBackToOnDemand_WhenProfileStale()
    {
        var seller = BuildEligibleSeller();
        // Profile com 72h (>48h threshold) → falls back
        var staleProfile = FellowCore.Domain.Entities.SellerRiskProfile.CreateOrUpdate(
            seller.Id, capturedCount: 100, lostCount: 0, capturedVolume: 50_000m,
            now: _time.GetUtcNow().UtcDateTime.AddHours(-72));
        _profileRepo.GetBySellerIdAsync(seller.Id).Returns(staleProfile);
        // Stats on-demand mostra rate alto (≠ profile stale)
        _txRepo.GetSellerRiskStatsAsync(seller.Id, Arg.Any<DateTime>())
            .Returns(new SellerRiskStats(100, 5, 0.05m));

        var result = await _sut.EvaluateAsync(BuildCreditTx(), seller);

        result.IsEligible.Should().BeFalse();
        // Confirma que veio do fallback on-demand (não do profile stale)
        await _txRepo.Received(1).GetSellerRiskStatsAsync(seller.Id, Arg.Any<DateTime>());
    }

    [Fact]
    public async Task R2_BoundaryExactMinAge_Allows()
    {
        // Idade exatamente igual ao mínimo (30 dias) deve passar
        var seller = BuildEligibleSeller(ageDays: 30);

        var result = await _sut.EvaluateAsync(BuildCreditTx(), seller);

        result.IsEligible.Should().BeTrue();
    }
}

/// <summary>TimeProvider mockável pra testes determinísticos.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
