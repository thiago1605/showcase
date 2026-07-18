using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Application.Modules.Payouts.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;

namespace FellowCore.Application.Tests.Services;

/// <summary>
/// Cobertura do <see cref="WithdrawService"/> — flow comercial spec 2026:
/// min R$ 50, max per-seller, fee R$ 1 / +1% D+0, cap diário Woovi R$ 48.800,
/// scheduling D+1 e cap-excedido pra fila FIFO.
/// </summary>
public class WithdrawServiceTests
{
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly IPayoutRepository _payoutRepository = Substitute.For<IPayoutRepository>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly IPayoutProcessor _payoutProcessor = Substitute.For<IPayoutProcessor>();
    private readonly WithdrawService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();

    public WithdrawServiceTests()
    {
        _sut = new WithdrawService(
            _sellerRepository,
            _payoutRepository,
            _ledgerService,
            _payoutProcessor,
            NullLogger<WithdrawService>.Instance);
    }

    private static Seller CreateSeller(decimal maxPerRequest = 5_000m)
    {
        var seller = Seller.Create(
            tenantId: TenantId,
            legalName: "Test Seller",
            document: "12345678901",
            email: "test@test.com",
            webhookSecret: "secret123456789012345678901234");
        if (maxPerRequest != 5_000m)
            seller.SetMaxWithdrawPerRequest(maxPerRequest);
        return seller;
    }

    [Fact]
    public async Task RequestAsync_ShouldThrowMinimumWithdraw_WhenBelowMinimum()
    {
        var seller = CreateSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);

        var act = () => _sut.RequestAsync(TenantId, seller.Id, 30m, WithdrawType.D1);

        await act.Should().ThrowAsync<MinimumWithdrawException>();
    }

    [Fact]
    public async Task RequestAsync_ShouldThrowIndividualLimit_WhenAboveMaxPerRequest()
    {
        var seller = CreateSeller(maxPerRequest: 1_000m);
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);

        var act = () => _sut.RequestAsync(TenantId, seller.Id, 5_000m, WithdrawType.D1);

        await act.Should().ThrowAsync<IndividualWithdrawLimitException>();
    }

    [Fact]
    public async Task RequestAsync_ShouldChargeR1Fee_WhenWithdrawBelow500()
    {
        var seller = CreateSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);
        _payoutRepository.GetTodayTotalGrossAsync(Arg.Any<DateTime>()).Returns(0m);
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), Arg.Any<Seller>())
            .Returns(new PayoutResult(Success: true, TransactionId: "tx-1"));

        var result = await _sut.RequestAsync(TenantId, seller.Id, 200m, WithdrawType.D0);

        // Fee = R$ 1 (small) + R$ 2 (1% D0) = R$ 3
        result.Fee.Should().Be(3m);
        result.NetAmount.Should().Be(197m);
    }

    [Fact]
    public async Task RequestAsync_ShouldChargeOnlyD0Fee_WhenAboveSmallThreshold()
    {
        var seller = CreateSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);
        _payoutRepository.GetTodayTotalGrossAsync(Arg.Any<DateTime>()).Returns(0m);
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), Arg.Any<Seller>())
            .Returns(new PayoutResult(Success: true, TransactionId: "tx-1"));

        var result = await _sut.RequestAsync(TenantId, seller.Id, 1_000m, WithdrawType.D0);

        // Fee = 0 (sem R$1 pq ≥ R$500) + R$ 10 (1% D0)
        result.Fee.Should().Be(10m);
        result.NetAmount.Should().Be(990m);
    }

    [Fact]
    public async Task RequestAsync_ShouldNotChargeFee_WhenD1AndAboveSmallThreshold()
    {
        var seller = CreateSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);
        _payoutRepository.GetTodayTotalGrossAsync(Arg.Any<DateTime>()).Returns(0m);

        var result = await _sut.RequestAsync(TenantId, seller.Id, 1_000m, WithdrawType.D1);

        result.Fee.Should().Be(0m);
        result.NetAmount.Should().Be(1_000m);
        result.ScheduledFor.Should().NotBeNull("D+1 sempre agenda");
        result.Type.Should().Be(WithdrawType.D1);
    }

    [Fact]
    public async Task RequestAsync_ShouldScheduleForNextBusinessDay_WhenD1()
    {
        var seller = CreateSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);
        _payoutRepository.GetTodayTotalGrossAsync(Arg.Any<DateTime>()).Returns(0m);

        var result = await _sut.RequestAsync(TenantId, seller.Id, 100m, WithdrawType.D1);

        result.ScheduledFor.Should().NotBeNull();
        // Próximo dia útil — não é sábado nem domingo
        result.ScheduledFor!.Value.DayOfWeek.Should().NotBe(DayOfWeek.Saturday);
        result.ScheduledFor!.Value.DayOfWeek.Should().NotBe(DayOfWeek.Sunday);
        // Processor NÃO foi chamado (saque agendado, não imediato)
        await _payoutProcessor.DidNotReceive().ProcessAsync(Arg.Any<Payout>(), Arg.Any<Seller>());
    }

    [Fact]
    public async Task RequestAsync_ShouldScheduleForD1_WhenD0ButExceedsDailyCap()
    {
        var seller = CreateSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);
        // Cap diário R$ 48.800; já usei R$ 48.000 hoje. Saque de R$ 1.000 supera.
        _payoutRepository.GetTodayTotalGrossAsync(Arg.Any<DateTime>())
            .Returns(PixLimits.DailyOutboundLimitBusinessHours - 800m);

        var result = await _sut.RequestAsync(TenantId, seller.Id, 1_000m, WithdrawType.D0);

        result.ScheduledFor.Should().NotBeNull();
        result.Message.Should().Contain("Cap diário");
        // Processor NÃO chamado mesmo sendo D+0
        await _payoutProcessor.DidNotReceive().ProcessAsync(Arg.Any<Payout>(), Arg.Any<Seller>());
    }

    [Fact]
    public async Task RequestAsync_ShouldDebitLedger_WithNetAmount()
    {
        var seller = CreateSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);
        _payoutRepository.GetTodayTotalGrossAsync(Arg.Any<DateTime>()).Returns(0m);
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), Arg.Any<Seller>())
            .Returns(new PayoutResult(Success: true, TransactionId: "tx-1"));

        await _sut.RequestAsync(TenantId, seller.Id, 1_000m, WithdrawType.D0);

        // Ledger debitado pelo net (R$ 1.000 - R$ 10 fee = R$ 990)
        await _ledgerService.Received(1).DebitSellerAsync(
            TenantId, seller.Id, 990m, Arg.Any<string>(), Arg.Any<string>());
        // Fee separada
        await _ledgerService.Received(1).DebitPayoutFeeAsync(
            TenantId, seller.Id, 10m, Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RequestAsync_ShouldMarkPaidImmediately_WhenD0AndProcessorSucceeds()
    {
        var seller = CreateSeller();
        _sellerRepository.GetByIdAsync(TenantId, seller.Id).Returns(seller);
        _payoutRepository.GetTodayTotalGrossAsync(Arg.Any<DateTime>()).Returns(0m);
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), Arg.Any<Seller>())
            .Returns(new PayoutResult(Success: true, TransactionId: "tx-success"));

        var result = await _sut.RequestAsync(TenantId, seller.Id, 1_000m, WithdrawType.D0);

        result.Status.Should().Be(PayoutStatus.PAID);
        result.ScheduledFor.Should().BeNull();
    }
}
