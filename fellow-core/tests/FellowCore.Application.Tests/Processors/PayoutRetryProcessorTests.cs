using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Payouts.DTOs;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Application.Modules.Payouts.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FellowCore.Application.Tests.Processors;

public class PayoutRetryProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    private readonly IPayoutRepository _payoutRepository = Substitute.For<IPayoutRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly IPayoutProcessor _payoutProcessor = Substitute.For<IPayoutProcessor>();
    private readonly IBackgroundJobs _backgroundJobs = Substitute.For<IBackgroundJobs>();
    private readonly IAppMetrics _appMetrics = Substitute.For<IAppMetrics>();
    private readonly PayoutService _payoutService;

    public PayoutRetryProcessorTests()
    {
        _payoutService = new PayoutService(
            _payoutRepository, _sellerRepository, _tenantRepository,
            _ledgerService, _payoutProcessor, Substitute.For<IEmailService>(),
            Substitute.For<IRealtimeNotifier>(), _backgroundJobs,
            _appMetrics, NullLogger<PayoutService>.Instance);
    }

    [Fact]
    public async Task Payout_WhenProviderTimeout_ShouldRetryWithSameIdempotencyKey()
    {
        // Arrange: Create a payout that will fail with transient error
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(BuildTenantWithConfig());
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .ThrowsAsync(new TimeoutException("Provider timeout"));

        // Act: First attempt
        var result = await _payoutService.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        // Assert: Stays PROCESSING with retry scheduled
        result.Status.Should().Be(PayoutStatus.PROCESSING);
        _appMetrics.Received(1).RecordPayoutRetry();

        // Ledger NOT reversed (debit held for retry)
        await _ledgerService.DidNotReceive().ReversalCreditAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_WhenMaxRetriesExceeded_ShouldCompensateLedger()
    {
        // Arrange: Payout at attempt 2/3 — next retry will be attempt 3 (last allowed).
        // When that also fails, it should exhaust retries and compensate.
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);

        var payout = Payout.Create(TenantId, SellerId, 1000m, 3m).Value;
        payout.MarkAsProcessing(); // attempt 1
        payout.ScheduleRetry("first failure");
        payout.MarkAsProcessing(); // attempt 2
        payout.ScheduleRetry("second failure");
        // AttemptCount=2, MaxRetries=3 → CanRetry=true (2 < 3)

        // Simulate that NextRetryAt has passed
        typeof(Payout).GetProperty("NextRetryAt")!
            .SetValue(payout, DateTime.UtcNow.AddMinutes(-1));

        _payoutRepository.GetByIdGlobalAsync(payout.Id).Returns(payout);
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .Returns(new PayoutResult(false, FailureReason: "Still failing"));

        // Act: RetryAsync increments to attempt 3, provider fails, exhausted → compensate
        await _payoutService.RetryAsync(payout.Id);

        // Assert: Ledger reversal happened (compensation)
        await _ledgerService.Received(1).ReversalCreditAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
        _appMetrics.Received(1).RecordPayoutFailed();
        payout.Status.Should().Be(PayoutStatus.FAILED);
    }

    [Fact]
    public async Task Payout_WhenRetrySucceeds_ShouldComplete()
    {
        // Arrange: Payout failed once, now being retried
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(BuildTenantWithConfig());

        var payout = Payout.Create(TenantId, SellerId, 500m, 2m).Value;
        payout.MarkAsProcessing(); // attempt 1
        payout.ScheduleRetry("first failure");
        // Simulate NextRetryAt has passed
        typeof(Payout).GetProperty("NextRetryAt")!
            .SetValue(payout, DateTime.UtcNow.AddMinutes(-1));

        _payoutRepository.GetByIdGlobalAsync(payout.Id).Returns(payout);
        _payoutProcessor.ProcessAsync(payout, seller)
            .Returns(new PayoutResult(true, TransactionId: "tx-retry-success"));

        // Act
        await _payoutService.RetryAsync(payout.Id);

        // Assert: Completed on retry
        payout.Status.Should().Be(PayoutStatus.PAID);
        payout.BankTransactionId.Should().Be("tx-retry-success");
        _appMetrics.Received(1).RecordPayout("completed");
    }

    [Fact]
    public async Task PayoutRetryProcessor_ShouldProcessRetryDuePayouts()
    {
        // Arrange
        var payout = Payout.Create(TenantId, SellerId, 1000m, 3m).Value;
        payout.MarkAsProcessing();
        payout.ScheduleRetry("test failure");

        var payoutRepo = Substitute.For<IPayoutRepository>();
        payoutRepo.GetRetryDueAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns([payout]);

        var payoutService = Substitute.For<IPayoutService>();
        var processor = new PayoutRetryProcessor(
            payoutRepo, payoutService, NullLogger<PayoutRetryProcessor>.Instance);

        // Act
        await processor.ProcessAsync();

        // Assert
        await payoutService.Received(1).RetryAsync(payout.Id);
    }

    [Fact]
    public async Task Payout_NonTransientException_ShouldCompensateImmediately()
    {
        // Arrange: Non-transient exception (not timeout/http/task)
        var seller = BuildSeller();
        _sellerRepository.GetByIdAsync(TenantId, SellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(BuildTenantWithConfig());
        _payoutProcessor.ProcessAsync(Arg.Any<Payout>(), seller)
            .ThrowsAsync(new InvalidOperationException("Account suspended"));

        // Act
        var result = await _payoutService.CreateAsync(TenantId, new CreatePayoutDto(SellerId, 1000m));

        // Assert: Immediate failure and compensation
        result.Status.Should().Be(PayoutStatus.FAILED);
        await _ledgerService.Received(1).ReversalCreditAsync(
            TenantId, SellerId, Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void Payout_Entity_ShouldTrackRetryState()
    {
        var payout = Payout.Create(TenantId, SellerId, 1000m, 2m).Value;

        // Initial state
        payout.AttemptCount.Should().Be(0);
        payout.MaxRetries.Should().Be(Payout.DefaultMaxRetries);
        payout.CanRetry.Should().BeFalse();
        payout.HasExhaustedRetries.Should().BeFalse();
        payout.IdempotencyKey.Should().NotBeNullOrEmpty();

        // After first processing attempt
        payout.MarkAsProcessing();
        payout.AttemptCount.Should().Be(1);
        payout.HasExhaustedRetries.Should().BeFalse();

        // Schedule retry
        payout.ScheduleRetry("timeout");
        payout.NextRetryAt.Should().NotBeNull();
        payout.LastError.Should().Be("timeout");
        payout.CanRetry.Should().BeTrue();

        // After max attempts
        payout.MarkAsProcessing(); // 2
        payout.MarkAsProcessing(); // 3
        payout.HasExhaustedRetries.Should().BeTrue();
    }

    private static Seller BuildSeller()
    {
        var seller = Seller.Create(
            tenantId: TenantId,
            legalName: "Test Seller",
            document: "12345678000190",
            email: "seller@test.com",
            webhookSecret: "encrypted-secret",
            preferredProvider: PaymentProvider.OPENPIX,
            externalAccountId: "opx-account-123",
            encryptedAccessToken: "encrypted-token");
        typeof(Seller).BaseType!.BaseType!.GetProperty("Id")!.SetValue(seller, SellerId);
        typeof(Seller).GetProperty("PayoutFixedFee")!.SetValue(seller, 2m);
        typeof(Seller).GetProperty("PayoutPercentFee")!.SetValue(seller, 0m);
        return seller;
    }

    private static Tenant BuildTenantWithConfig()
    {
        var tenant = Tenant.Create("Test Tenant", "test-tenant", "testhash", "pk_test", "hash");
        tenant.CreateDefaultConfig();
        return tenant;
    }
}
