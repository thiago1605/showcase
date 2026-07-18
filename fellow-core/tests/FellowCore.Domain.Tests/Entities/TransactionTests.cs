using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Events;

namespace FellowCore.Domain.Tests.Entities;

public class TransactionTests
{
    private static Transaction CreateValidTransaction(decimal amount = 100m, Guid? sellerId = null) =>
        Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: amount,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 1.5m,
            netAmount: 98.5m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(1),
            providerTxId: "pay_001",
            sellerId: sellerId).Value;

    [Fact]
    public void Create_ShouldSucceed_WithValidData()
    {
        var result = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: 100m,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 1.5m,
            netAmount: 98.5m,
            expectedSettlementDate: null,
            providerTxId: "pay_001");

        result.IsSuccess.Should().BeTrue();
        result.Value.Amount.Should().Be(100m);
        result.Value.Status.Should().Be(TransactionStatus.PROCESSING);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Create_ShouldFail_WhenAmountIsZeroOrNegative(decimal amount)
    {
        var result = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: amount,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 0,
            netAmount: 0,
            expectedSettlementDate: null,
            providerTxId: null);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Transaction.InvalidAmount");
    }

    [Fact]
    public void Create_ShouldPersistFeeAllocationPolicy()
    {
        var result = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: 100m,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 1.5m,
            netAmount: 98.5m,
            expectedSettlementDate: null,
            providerTxId: "pay_001",
            feeAllocationPolicy: FeeAllocationPolicy.PROPORTIONAL_TO_RECIPIENTS);

        result.IsSuccess.Should().BeTrue();
        result.Value.FeeAllocationPolicy.Should().Be(FeeAllocationPolicy.PROPORTIONAL_TO_RECIPIENTS);
    }

    [Fact]
    public void Create_ShouldDefaultToSellerPaysFees_WhenNullPolicy()
    {
        var result = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: 100m,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 1.5m,
            netAmount: 98.5m,
            expectedSettlementDate: null,
            providerTxId: "pay_001",
            feeAllocationPolicy: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.FeeAllocationPolicy.Should().Be(FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES);
    }

    [Fact]
    public void Create_ShouldRaiseTransactionCreatedEvent()
    {
        var tenantId = Guid.NewGuid();

        var result = Transaction.Create(
            tenantId: tenantId,
            amount: 200m,
            paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 9m,
            netAmount: 191m,
            expectedSettlementDate: null,
            providerTxId: "pay_002");

        result.Value.DomainEvents.Should().ContainSingle(e => e is TransactionCreatedEvent);
        var @event = (TransactionCreatedEvent)result.Value.DomainEvents[0];
        @event.TenantId.Should().Be(tenantId);
        @event.Amount.Should().Be(200m);
        @event.PaymentType.Should().Be(PaymentType.CREDIT_CARD);
    }

    [Fact]
    public void UpdateStatus_ShouldChangeStatus_AndRaiseEvent()
    {
        var transaction = CreateValidTransaction();
        transaction.ClearDomainEvents();

        var updateResult = transaction.UpdateStatus(TransactionStatus.CAPTURED);

        updateResult.IsSuccess.Should().BeTrue();
        transaction.Status.Should().Be(TransactionStatus.CAPTURED);
        transaction.DomainEvents.Should().ContainSingle(e => e is TransactionStatusChangedEvent);

        var @event = (TransactionStatusChangedEvent)transaction.DomainEvents[0];
        @event.OldStatus.Should().Be(TransactionStatus.PROCESSING);
        @event.NewStatus.Should().Be(TransactionStatus.CAPTURED);
    }

    [Fact]
    public void UpdateStatus_ShouldReturnSuccess_WhenSameStatus()
    {
        var transaction = CreateValidTransaction();
        transaction.ClearDomainEvents();

        var result = transaction.UpdateStatus(TransactionStatus.PROCESSING);

        result.IsSuccess.Should().BeTrue();
        transaction.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void AddTimelineEvent_ShouldAddToTimeline()
    {
        var transaction = CreateValidTransaction();

        transaction.AddTimelineEvent(TransactionStatus.CAPTURED);

        transaction.Timeline.Should().ContainSingle(e => e.Status == TransactionStatus.CAPTURED);
    }

    [Fact]
    public void AddSplit_ShouldAddToSplits()
    {
        var transaction = CreateValidTransaction();

        transaction.AddSplit("recipient_001", "SELLER", 50m, 50.0m);

        transaction.Splits.Should().ContainSingle();
        transaction.Splits.First().Amount.Should().Be(50m);
    }

    [Fact]
    public void ClearDomainEvents_ShouldEmptyEvents()
    {
        var transaction = CreateValidTransaction();
        transaction.DomainEvents.Should().NotBeEmpty();

        transaction.ClearDomainEvents();

        transaction.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void SetPayerInfo_ShouldPersistEmailAndName()
    {
        var transaction = CreateValidTransaction();

        transaction.SetPayerInfo("payer@test.com", "Test Payer");

        transaction.PayerEmail.Should().Be("payer@test.com");
        transaction.PayerName.Should().Be("Test Payer");
    }

    [Fact]
    public void SetPayerInfo_ShouldAcceptNullValues()
    {
        var transaction = CreateValidTransaction();
        transaction.SetPayerInfo("payer@test.com", "Test Payer");

        transaction.SetPayerInfo(null, null);

        transaction.PayerEmail.Should().BeNull();
        transaction.PayerName.Should().BeNull();
    }
}
