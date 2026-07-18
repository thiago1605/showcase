using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Payouts.DTOs;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Application.Modules.Payouts.Services;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class PayoutServiceTests
{
    private readonly IPayoutRepository _payoutRepository = Substitute.For<IPayoutRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly IPayoutProcessor _payoutProcessor = Substitute.For<IPayoutProcessor>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IRealtimeNotifier _realtimeNotifier = Substitute.For<IRealtimeNotifier>();
    private readonly IBackgroundJobs _backgroundJobs = Substitute.For<IBackgroundJobs>();
    private readonly PayoutService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    public PayoutServiceTests()
    {
        _sut = new PayoutService(
            _payoutRepository, _sellerRepository, _tenantRepository,
            _ledgerService, _payoutProcessor, _emailService,
            _realtimeNotifier, _backgroundJobs,
            Substitute.For<IAppMetrics>(),
            Substitute.For<ILogger<PayoutService>>());
    }

    // --- CreateAsync: successful payout ---

    [Fact]
    public async Task CreateAsync_SuccessfulPayout_DebitsLedgerAndCallsProvider()
    {
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Returns(new PayoutResult(true, TransactionId: "tx-001"));
        _tenantRepository.GetByIdWithConfigAsync(TenantId)
            .Returns(BuildTenantWithConfig());

        var result = await _sut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        result.Should().NotBeNull();
        result.SellerId.Should().Be(SellerId);
        result.Amount.Should().Be(1000m);
        result.Status.Should().Be(PayoutStatus.PAID);

        // Verify ledger was debited (net amount)
        await _ledgerService.Received(1).DebitSellerAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());

        // Verify provider was called
        await _payoutProcessor.Received(1).ProcessAsync(Arg.Any<Payout>(), seller);

        // Verify payout was persisted
        _payoutRepository.Received(1).Add(Arg.Any<Payout>());
        _payoutRepository.Received(1).Update(Arg.Any<Payout>());
    }

    // --- CreateAsync: provider failure reverses ledger ---

    [Fact]
    public async Task CreateAsync_ProviderFailure_SchedulesRetry()
    {
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Returns(new PayoutResult(false, FailureReason: "Provider offline"));
        _tenantRepository.GetByIdWithConfigAsync(TenantId)
            .Returns(BuildTenantWithConfig());

        var result = await _sut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        // First failure with retries available → stays PROCESSING for retry
        result.Status.Should().Be(PayoutStatus.PROCESSING);

        // Ledger should NOT be reversed yet — retry pending
        await _ledgerService.DidNotReceive().ReversalCreditAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CreateAsync_ProviderThrowsException_ReversesLedgerDebit()
    {
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Throws(new Exception("Connection timeout"));
        _tenantRepository.GetByIdWithConfigAsync(TenantId)
            .Returns(BuildTenantWithConfig());

        var result = await _sut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        result.Status.Should().Be(PayoutStatus.FAILED);

        await _ledgerService.Received(1).ReversalCreditAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // --- CreateAsync: insufficient balance throws ---

    [Fact]
    public async Task CreateAsync_InsufficientBalance_ThrowsBusinessException()
    {
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);

        _ledgerService.DebitSellerAsync(TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>())
            .Throws(new BusinessException("Ledger.InsufficientBalance", "Saldo insuficiente na conta WALLET."));

        var act = () => _sut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*insuficiente*");
    }

    // --- CreateAsync: no external account throws ---

    [Fact]
    public async Task CreateAsync_NoExternalAccount_ThrowsBusinessException()
    {
        var seller = BuildSeller(hasExternalAccount: false);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);

        var act = () => _sut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*conta BaaS*");
    }

    // --- CreateAsync: seller not found ---

    [Fact]
    public async Task CreateAsync_SellerNotFound_ThrowsNotFoundException()
    {
        _sellerRepository.GetByIdAsync(TenantId, Arg.Any<Guid>()).Returns((Seller?)null);

        var act = () => _sut.CreateAsync(TenantId, new CreatePayoutDto(Guid.NewGuid(), 500m));

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Seller*");
    }

    // --- Fee calculation ---

    [Fact]
    public async Task CreateAsync_FeeCalculation_IncludesFixedAndPercentAndOpenPixFees()
    {
        var seller = BuildSeller(payoutFixedFee: 2m, payoutPercentFee: 1.5m);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Returns(new PayoutResult(true, TransactionId: "tx-fee-test"));
        _tenantRepository.GetByIdWithConfigAsync(TenantId)
            .Returns(BuildTenantWithConfig());

        // Amount = 100m, below R$500 OpenPix threshold
        // PercentFee = 100 * 1.5% = 1.50
        // CalculateTotalFee(100, 2) = 2 + 1 (OpenPix fee) = 3
        // Total fee = 3 + 1.50 = 4.50
        // Net = 100 - 4.50 = 95.50
        var result = await _sut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 100m));

        result.Fee.Should().Be(4.50m);

        // Verify that debit was called with net amount = 95.50
        await _ledgerService.Received(1).DebitSellerAsync(
            TenantId, SellerId, 95.50m, Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CreateAsync_FeeExceedsAmount_ThrowsBusinessException()
    {
        // Very small amount where fees exceed the payout
        var seller = BuildSeller(payoutFixedFee: 5m, payoutPercentFee: 50m);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);

        // Amount = 2m
        // PercentFee = 2 * 50% = 1.00
        // CalculateTotalFee(2, 5) = 5 + 1 (OpenPix) = 6
        // Total fee = 6 + 1.00 = 7.00
        // Net = 2 - 7 = -5 <= 0 -> throws
        var act = () => _sut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 2m));

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*taxas*");
    }

    [Fact]
    public async Task CreateAsync_AboveOpenPixThreshold_NoOpenPixFee()
    {
        var seller = BuildSeller(payoutFixedFee: 2m, payoutPercentFee: 1m);
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Returns(new PayoutResult(true, TransactionId: "tx-big"));
        _tenantRepository.GetByIdWithConfigAsync(TenantId)
            .Returns(BuildTenantWithConfig());

        // Amount = 600m, above R$500 threshold => no OpenPix fee
        // PercentFee = 600 * 1% = 6.00
        // CalculateTotalFee(600, 2) = 2 + 0 = 2
        // Total fee = 2 + 6 = 8.00
        // Net = 600 - 8 = 592
        var result = await _sut.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 600m));

        result.Fee.Should().Be(8.00m);
        await _ledgerService.Received(1).DebitSellerAsync(
            TenantId, SellerId, 592m, Arg.Any<string>(), Arg.Any<string>());
    }

    // --- Helpers ---

    private static Tenant BuildTenantWithConfig()
    {
        var tenant = Tenant.Create("Test Tenant", "test-tenant", "testhash", "pk_test_xxxx", "hash");
        tenant.CreateDefaultConfig();
        return tenant;
    }

    private static Seller BuildSeller(
        bool hasExternalAccount = true,
        decimal payoutFixedFee = 1m,
        decimal payoutPercentFee = 1.5m)
    {
        var seller = Seller.Create(
            tenantId: TenantId,
            legalName: "Test Seller",
            document: "12345678901",
            email: "seller@test.com",
            webhookSecret: "encrypted-secret",
            preferredProvider: PaymentProvider.OPENPIX,
            externalAccountId: hasExternalAccount ? "ext-account-123" : null,
            encryptedAccessToken: "encrypted-token"
        );

        typeof(Seller).BaseType!.BaseType!.GetProperty("Id")!.SetValue(seller, SellerId);

        // Set fee values via reflection since they have private setters
        typeof(Seller).GetProperty("PayoutFixedFee")!.SetValue(seller, payoutFixedFee);
        typeof(Seller).GetProperty("PayoutPercentFee")!.SetValue(seller, payoutPercentFee);

        return seller;
    }
}
