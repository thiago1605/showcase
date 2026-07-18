using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class ReceiptTests
{
    private static Receipt CreateReceipt() =>
        Receipt.Create(
            tenantId: Guid.NewGuid(),
            sellerId: Guid.NewGuid(),
            type: ReceiptType.PAYMENT,
            provider: PaymentProvider.STRIPE,
            amount: 100m,
            transactionId: Guid.NewGuid());

    [Fact]
    public void Create_ShouldInitializeWithCorrectDefaults()
    {
        var receipt = CreateReceipt();

        receipt.Id.Should().NotBeEmpty();
        receipt.Status.Should().Be(ReceiptStatus.GENERATED);
        receipt.CustomerEmailSentAt.Should().BeNull();
        receipt.CustomerEmailLastError.Should().BeNull();
        receipt.CustomerEmailAttempts.Should().Be(0);
        receipt.IsCustomerEmailSent.Should().BeFalse();
    }

    [Fact]
    public void MarkCustomerEmailSent_ShouldSetTimestampAndIncrementAttempts()
    {
        var receipt = CreateReceipt();

        receipt.MarkCustomerEmailSent();

        receipt.IsCustomerEmailSent.Should().BeTrue();
        receipt.CustomerEmailSentAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        receipt.CustomerEmailAttempts.Should().Be(1);
        receipt.CustomerEmailLastError.Should().BeNull();
    }

    [Fact]
    public void RecordCustomerEmailFailure_ShouldRecordErrorAndIncrementAttempts()
    {
        var receipt = CreateReceipt();

        receipt.RecordCustomerEmailFailure("SMTP timeout");

        receipt.IsCustomerEmailSent.Should().BeFalse();
        receipt.CustomerEmailLastError.Should().Be("SMTP timeout");
        receipt.CustomerEmailAttempts.Should().Be(1);
    }

    [Fact]
    public void RecordCustomerEmailFailure_ShouldTruncateLongErrors()
    {
        var receipt = CreateReceipt();
        var longError = new string('x', 1000);

        receipt.RecordCustomerEmailFailure(longError);

        receipt.CustomerEmailLastError!.Length.Should().Be(500);
    }

    [Fact]
    public void MarkCustomerEmailSent_ShouldClearPreviousError()
    {
        var receipt = CreateReceipt();
        receipt.RecordCustomerEmailFailure("Previous error");

        receipt.MarkCustomerEmailSent();

        receipt.CustomerEmailLastError.Should().BeNull();
        receipt.IsCustomerEmailSent.Should().BeTrue();
        receipt.CustomerEmailAttempts.Should().Be(2); // failure + success
    }

    [Fact]
    public void SetPublicUrl_ShouldChangeStatusToAvailable()
    {
        var receipt = CreateReceipt();

        receipt.SetPublicUrl("https://example.com/receipt/123");

        receipt.PublicUrl.Should().Be("https://example.com/receipt/123");
        receipt.Status.Should().Be(ReceiptStatus.AVAILABLE);
    }

    [Fact]
    public void MarkFailed_ShouldChangeStatus()
    {
        var receipt = CreateReceipt();

        receipt.MarkFailed();

        receipt.Status.Should().Be(ReceiptStatus.FAILED);
    }
}
