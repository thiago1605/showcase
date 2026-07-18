using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.Extensions.Logging.Abstractions;

namespace FellowCore.Application.Tests.Processors;

public class RefundRetryProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    [Fact]
    public void RefundIntent_Entity_ShouldTrackRetryState()
    {
        var intent = RefundIntent.Create(TenantId, Guid.NewGuid(), 100m, "test", "key-1");

        intent.AttemptCount.Should().Be(0);
        intent.MaxRetries.Should().Be(RefundIntent.DefaultMaxRetries);
        intent.CanRetry.Should().BeFalse();

        // Mark processing (attempt 1)
        intent.MarkProcessing();
        intent.AttemptCount.Should().Be(1);
        intent.Status.Should().Be(RefundIntentStatus.PROCESSING);

        // Schedule retry
        intent.ScheduleRetry("provider timeout");
        intent.NextRetryAt.Should().NotBeNull();
        intent.LastError.Should().Be("provider timeout");
        intent.CanRetry.Should().BeTrue();

        // Complete
        intent.Complete("re_123");
        intent.Status.Should().Be(RefundIntentStatus.COMPLETED);
        intent.NextRetryAt.Should().BeNull();
    }

    [Fact]
    public void RefundIntent_ShouldDetectExhaustedRetries()
    {
        var intent = RefundIntent.Create(TenantId, Guid.NewGuid(), 100m);

        intent.MarkProcessing(); // 1
        intent.MarkProcessing(); // 2
        intent.MarkProcessing(); // 3

        intent.HasExhaustedRetries.Should().BeTrue();
    }

    [Fact]
    public async Task RefundRetryProcessor_ShouldRetryDueIntents()
    {
        var transactionId = Guid.NewGuid();
        var transaction = Transaction.Create(
            TenantId, 500m, PaymentType.CREDIT_CARD, PaymentProvider.STRIPE,
            1, 10m, 490m, DateTime.UtcNow.AddDays(30),
            null, SellerId).Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(transaction, transactionId);
        typeof(Transaction).GetProperty("ProviderTxId")!.SetValue(transaction, "pi_test");
        // Transaction precisa estar CAPTURED pra refund passar a validação de domínio
        typeof(Transaction).GetProperty("Status")!.SetValue(transaction, TransactionStatus.CAPTURED);

        var intent = RefundIntent.Create(TenantId, transactionId, 100m, "test", "refund_key");
        intent.MarkProcessing(); // attempt 1
        intent.ScheduleRetry("timeout");
        // Set NextRetryAt to past
        typeof(RefundIntent).GetProperty("NextRetryAt")!
            .SetValue(intent, DateTime.UtcNow.AddMinutes(-1));

        var refundIntentRepo = Substitute.For<IRefundIntentRepository>();
        refundIntentRepo.GetRetryDueAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns([intent]);

        var txRepo = Substitute.For<ITransactionRepository>();
        txRepo.GetByIdWithTimelineAsync(TenantId, transactionId).Returns(transaction);

        var tenant = Tenant.Create("Test", "test-slug", "hash", "pk_test", "whsec");
        typeof(Tenant).GetProperty("Id")!.SetValue(tenant, TenantId);
        var tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var sellerRepo = Substitute.For<ISellerRepository>();

        var rail = Substitute.For<IPaymentRail>();
        rail.ExecuteRefundAsync(Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<string>(),
            Arg.Any<decimal>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("re_retry_success");

        var railRouter = Substitute.For<IRailRouter>();
        railRouter.ResolveRailForTransaction(transaction).Returns(rail);

        var processor = new RefundRetryProcessor(
            refundIntentRepo, txRepo, tenantRepo, sellerRepo,
            railRouter,
            Substitute.For<ILedgerService>(),
            Substitute.For<IBackgroundJobs>(),
            Substitute.For<IAppMetrics>(),
            NullLogger<RefundRetryProcessor>.Instance);

        // Act
        await processor.ProcessAsync();

        // Assert intent state
        intent.Status.Should().Be(RefundIntentStatus.COMPLETED);
        intent.ProviderRefundId.Should().Be("re_retry_success");
        refundIntentRepo.Received().Update(intent);

        // Assert TRANSACTION também foi mutada (sem isso, ledger fica out-of-sync)
        transaction.RefundedAmount.Should().Be(100m);
    }

    [Fact]
    public async Task RefundRetryProcessor_WhenMaxRetriesExhausted_ShouldFail()
    {
        var transactionId = Guid.NewGuid();
        var transaction = Transaction.Create(
            TenantId, 500m, PaymentType.CREDIT_CARD, PaymentProvider.STRIPE,
            1, 10m, 490m, DateTime.UtcNow.AddDays(30),
            null, SellerId).Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(transaction, transactionId);
        typeof(Transaction).GetProperty("ProviderTxId")!.SetValue(transaction, "pi_test");

        var intent = RefundIntent.Create(TenantId, transactionId, 100m, "test", "refund_key");
        intent.MarkProcessing(); // 1
        intent.ScheduleRetry("fail 1");
        intent.MarkProcessing(); // 2
        intent.ScheduleRetry("fail 2");
        // Set NextRetryAt to past for processor pickup
        typeof(RefundIntent).GetProperty("NextRetryAt")!
            .SetValue(intent, DateTime.UtcNow.AddMinutes(-1));

        var refundIntentRepo = Substitute.For<IRefundIntentRepository>();
        refundIntentRepo.GetRetryDueAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns([intent]);

        var txRepo = Substitute.For<ITransactionRepository>();
        txRepo.GetByIdWithTimelineAsync(TenantId, transactionId).Returns(transaction);

        var tenant = Tenant.Create("Test", "test-slug", "hash", "pk_test", "whsec");
        typeof(Tenant).GetProperty("Id")!.SetValue(tenant, TenantId);
        var tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var rail = Substitute.For<IPaymentRail>();
        rail.ExecuteRefundAsync(Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<string>(),
            Arg.Any<decimal>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns<string?>(_ => throw new TimeoutException("still timing out"));

        var railRouter = Substitute.For<IRailRouter>();
        railRouter.ResolveRailForTransaction(transaction).Returns(rail);

        var backgroundJobs = Substitute.For<IBackgroundJobs>();

        var processor = new RefundRetryProcessor(
            refundIntentRepo, txRepo, tenantRepo, Substitute.For<ISellerRepository>(),
            railRouter,
            Substitute.For<ILedgerService>(),
            backgroundJobs,
            Substitute.For<IAppMetrics>(),
            NullLogger<RefundRetryProcessor>.Instance);

        // Act
        await processor.ProcessAsync();

        // Assert: exhausted retries → FAILED
        intent.Status.Should().Be(RefundIntentStatus.FAILED);
    }
}
