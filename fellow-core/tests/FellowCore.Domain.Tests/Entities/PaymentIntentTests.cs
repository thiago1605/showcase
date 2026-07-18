using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class PaymentIntentTests
{
    private static PaymentIntent BuildIntent() =>
        PaymentIntent.Create(Guid.NewGuid(), "order-001", 500m, sellerId: Guid.NewGuid());

    [Fact]
    public void Create_ShouldInitializeWithPendingStatus()
    {
        var intent = BuildIntent();

        intent.Status.Should().Be(PaymentIntentStatus.PENDING);
        intent.CapturedTransactionId.Should().BeNull();
        intent.IsAlreadyCaptured.Should().BeFalse();
        intent.ExternalReferenceId.Should().Be("order-001");
        intent.Amount.Should().Be(500m);
    }

    [Fact]
    public void StartProcessing_ShouldSetRailAndStatus()
    {
        var intent = BuildIntent();

        intent.StartProcessing(PaymentRailType.STRIPE_CARD);

        intent.Status.Should().Be(PaymentIntentStatus.PROCESSING);
        intent.SelectedRail.Should().Be(PaymentRailType.STRIPE_CARD);
    }

    [Fact]
    public void TryCapture_ShouldSucceed_WhenFirstCapture()
    {
        var intent = BuildIntent();
        var txId = Guid.NewGuid();

        var result = intent.TryCapture(txId);

        result.Should().BeTrue();
        intent.Status.Should().Be(PaymentIntentStatus.CAPTURED);
        intent.CapturedTransactionId.Should().Be(txId);
        intent.IsAlreadyCaptured.Should().BeTrue();
    }

    [Fact]
    public void TryCapture_ShouldFail_WhenAlreadyCaptured()
    {
        var intent = BuildIntent();
        var firstTx = Guid.NewGuid();
        var secondTx = Guid.NewGuid();

        intent.TryCapture(firstTx);
        var result = intent.TryCapture(secondTx);

        result.Should().BeFalse();
        intent.CapturedTransactionId.Should().Be(firstTx);
    }

    [Fact]
    public void StartProcessing_ShouldBeIgnored_WhenAlreadyCaptured()
    {
        var intent = BuildIntent();
        intent.TryCapture(Guid.NewGuid());

        intent.StartProcessing(PaymentRailType.OPENPIX_PIX);

        intent.Status.Should().Be(PaymentIntentStatus.CAPTURED);
    }

    [Fact]
    public void Cancel_ShouldBeIgnored_WhenAlreadyCaptured()
    {
        var intent = BuildIntent();
        intent.TryCapture(Guid.NewGuid());

        intent.Cancel();

        intent.Status.Should().Be(PaymentIntentStatus.CAPTURED);
    }

    [Fact]
    public void MarkFailed_ShouldSetStatus_WhenNotCaptured()
    {
        var intent = BuildIntent();
        intent.StartProcessing(PaymentRailType.STRIPE_BOLETO);

        intent.MarkFailed();

        intent.Status.Should().Be(PaymentIntentStatus.FAILED);
    }

    [Fact]
    public void MarkDisputed_ShouldSetStatus()
    {
        var intent = BuildIntent();
        intent.TryCapture(Guid.NewGuid());

        intent.MarkDisputed();

        intent.Status.Should().Be(PaymentIntentStatus.DISPUTED);
    }

    [Fact]
    public void MarkRefunded_ShouldSetStatus()
    {
        var intent = BuildIntent();
        intent.TryCapture(Guid.NewGuid());

        intent.MarkRefunded();

        intent.Status.Should().Be(PaymentIntentStatus.REFUNDED);
    }

    [Fact]
    public void MarkPartiallyRefunded_ShouldSetStatus()
    {
        var intent = BuildIntent();
        intent.TryCapture(Guid.NewGuid());

        intent.MarkPartiallyRefunded();

        intent.Status.Should().Be(PaymentIntentStatus.PARTIALLY_REFUNDED);
    }
}
