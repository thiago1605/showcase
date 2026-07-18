using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Modules.Subscriptions.Services;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class SubscriptionBillingProcessorTests
{
    private readonly ISubscriptionRepository _subscriptionRepo = Substitute.For<ISubscriptionRepository>();
    private readonly ISellerRepository _sellerRepo = Substitute.For<ISellerRepository>();
    private readonly ITransactionService _transactionService = Substitute.For<ITransactionService>();
    private readonly ILogger<SubscriptionBillingProcessor> _logger = Substitute.For<ILogger<SubscriptionBillingProcessor>>();
    private readonly SubscriptionBillingProcessor _sut;

    public SubscriptionBillingProcessorTests()
    {
        // Default: seller exists and is ACTIVE
        _sellerRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(callInfo => Seller.Create(
                callInfo.ArgAt<Guid>(0), "Test Seller", "12345678000199",
                "seller@test.com", "webhook-secret-32-chars-ok!!!!!!"));

        _sut = new SubscriptionBillingProcessor(_subscriptionRepo, _sellerRepo, _transactionService, _logger);
    }

    [Fact]
    public async Task ProcessDueBillingAsync_WithNoDueSubscriptions_DoesNothing()
    {
        _subscriptionRepo.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription>());

        await _sut.ProcessDueBillingAsync();

        await _transactionService.DidNotReceive()
            .CreateAsync(Arg.Any<Guid>(), Arg.Any<CreateTransactionDto>());
    }

    [Fact]
    public async Task ProcessDueBillingAsync_WithDueSubscription_CreatesTransaction()
    {
        var sub = BuildDueSubscription();
        _subscriptionRepo.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription> { sub });

        _transactionService.CreateAsync(sub.TenantId, Arg.Any<CreateTransactionDto>())
            .Returns(new TransactionResponseDto(
                Guid.NewGuid(), TransactionStatus.CREATED, 99.90m,
                new GatewayPaymentDetails("tx-123")));

        await _sut.ProcessDueBillingAsync();

        await _transactionService.Received(1)
            .CreateAsync(sub.TenantId, Arg.Is<CreateTransactionDto>(dto =>
                dto.Amount == 99.90m &&
                dto.SellerId == sub.SellerId &&
                dto.PaymentType == PaymentType.PIX));

        _subscriptionRepo.Received(1).Update(sub);
        await _subscriptionRepo.Received(1).SaveChangesAsync();

        sub.CycleCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessDueBillingAsync_WithMultipleDue_ProcessesAll()
    {
        var sub1 = BuildDueSubscription();
        var sub2 = BuildDueSubscription(amount: 200m);
        _subscriptionRepo.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription> { sub1, sub2 });

        _transactionService.CreateAsync(Arg.Any<Guid>(), Arg.Any<CreateTransactionDto>())
            .Returns(new TransactionResponseDto(
                Guid.NewGuid(), TransactionStatus.CREATED, 100m,
                new GatewayPaymentDetails("tx-mock")));

        await _sut.ProcessDueBillingAsync();

        await _transactionService.Received(2)
            .CreateAsync(Arg.Any<Guid>(), Arg.Any<CreateTransactionDto>());
    }

    [Fact]
    public async Task ProcessDueBillingAsync_WhenTransactionFails_ContinuesWithNext()
    {
        var sub1 = BuildDueSubscription();
        var sub2 = BuildDueSubscription(amount: 150m);
        _subscriptionRepo.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription> { sub1, sub2 });

        // First fails, second succeeds
        _transactionService.CreateAsync(sub1.TenantId, Arg.Any<CreateTransactionDto>())
            .Throws(new Exception("Payment gateway error"));

        _transactionService.CreateAsync(sub2.TenantId, Arg.Any<CreateTransactionDto>())
            .Returns(new TransactionResponseDto(
                Guid.NewGuid(), TransactionStatus.CREATED, 150m,
                new GatewayPaymentDetails("tx-ok")));

        await _sut.ProcessDueBillingAsync();

        // sub1 failed — cycle NOT advanced
        sub1.CycleCount.Should().Be(0);

        // sub2 succeeded — cycle advanced
        sub2.CycleCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessDueBillingAsync_SetsIdempotencyKey()
    {
        var sub = BuildDueSubscription();
        _subscriptionRepo.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription> { sub });

        _transactionService.CreateAsync(Arg.Any<Guid>(), Arg.Any<CreateTransactionDto>())
            .Returns(new TransactionResponseDto(
                Guid.NewGuid(), TransactionStatus.CREATED, 99.90m,
                new GatewayPaymentDetails("tx-123")));

        await _sut.ProcessDueBillingAsync();

        await _transactionService.Received(1)
            .CreateAsync(Arg.Any<Guid>(), Arg.Is<CreateTransactionDto>(dto =>
                dto.IdempotencyKey != null &&
                dto.IdempotencyKey.StartsWith("sub-") &&
                dto.IdempotencyKey.Contains("cycle-1")));
    }

    [Fact]
    public async Task ProcessDueBillingAsync_WhenCancelled_StopsProcessing()
    {
        var sub1 = BuildDueSubscription();
        var sub2 = BuildDueSubscription(amount: 200m);
        _subscriptionRepo.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription> { sub1, sub2 });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await _sut.ProcessDueBillingAsync(cts.Token);

        await _transactionService.DidNotReceive()
            .CreateAsync(Arg.Any<Guid>(), Arg.Any<CreateTransactionDto>());
    }

    [Fact]
    public async Task ProcessDueBillingAsync_WithMaxCyclesReached_ExpiresSubscription()
    {
        var sub = BuildDueSubscription(maxCycles: 1);
        _subscriptionRepo.GetDueForBillingAsync(Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<Subscription> { sub });

        _transactionService.CreateAsync(Arg.Any<Guid>(), Arg.Any<CreateTransactionDto>())
            .Returns(new TransactionResponseDto(
                Guid.NewGuid(), TransactionStatus.CREATED, 99.90m,
                new GatewayPaymentDetails("tx-123")));

        await _sut.ProcessDueBillingAsync();

        sub.CycleCount.Should().Be(1);
        sub.Status.Should().Be(SubscriptionStatus.EXPIRED);
    }

    private static Subscription BuildDueSubscription(decimal amount = 99.90m, int? maxCycles = null)
    {
        var past = DateTime.UtcNow.AddHours(-1);
        return Subscription.Create(
            Guid.NewGuid(), Guid.NewGuid(), amount,
            "Plano Teste", BillingInterval.MONTHLY,
            startDate: past, maxCycles: maxCycles).Value;
    }
}
