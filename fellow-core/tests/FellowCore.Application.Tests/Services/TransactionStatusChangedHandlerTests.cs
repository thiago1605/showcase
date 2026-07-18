using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Notifications.DTOs;
using FellowCore.Application.Modules.Notifications.Handlers;
using FellowCore.Application.Modules.Notifications.Interfaces;
using FellowCore.Application.Modules.Receipts;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Events;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class TransactionStatusChangedHandlerTests
{
    private readonly INotificationsService _notificationsService = Substitute.For<INotificationsService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IRealtimeNotifier _realtimeNotifier = Substitute.For<IRealtimeNotifier>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly ISellerRepository _sellerRepo = Substitute.For<ISellerRepository>();
    private readonly ICustomerRepository _customerRepo = Substitute.For<ICustomerRepository>();
    private readonly ITransactionRepository _transactionRepo = Substitute.For<ITransactionRepository>();
    private readonly IReceiptService _receiptService = Substitute.For<IReceiptService>();
    private readonly IReceiptRepository _receiptRepo = Substitute.For<IReceiptRepository>();
    private readonly ISplitProcessor _splitProcessor = Substitute.For<ISplitProcessor>();
    private readonly ILogger<TransactionStatusChangedHandler> _logger = Substitute.For<ILogger<TransactionStatusChangedHandler>>();

    private readonly TransactionStatusChangedHandler _sut;

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();
    private static readonly Guid TransactionId = Guid.NewGuid();

    public TransactionStatusChangedHandlerTests()
    {
        _sut = new TransactionStatusChangedHandler(
            _notificationsService,
            _emailService,
            _realtimeNotifier,
            _tenantRepo,
            _sellerRepo,
            _customerRepo,
            _transactionRepo,
            _receiptService,
            _receiptRepo,
            _splitProcessor,
            _logger);
    }

    [Fact]
    public async Task HandleAsync_WhenCaptured_SendsCustomerReceiptEmail()
    {
        // Arrange
        var tx = BuildCapturedTransaction(payerEmail: "payer@example.com", payerName: "John Doe");
        _transactionRepo.GetByIdAsync(TenantId, TransactionId).Returns(tx);

        var receipt = Receipt.Create(TenantId, SellerId, ReceiptType.PAYMENT, PaymentProvider.STRIPE, 100m, transactionId: TransactionId);
        _receiptService.GenerateForPaymentAsync(TenantId, TransactionId).Returns(receipt);

        var tenant = Tenant.Create("Test Tenant", "test-tenant", "hash", "fp_", "secret_hash", "owner@test.com");
        _tenantRepo.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var seller = Seller.Create(TenantId, "Seller LTDA", "12345678000100", "seller@test.com", "whsec_test");
        _sellerRepo.GetByIdAsync(TenantId, SellerId).Returns(seller);

        var domainEvent = BuildCapturedEvent();

        // Act
        await _sut.HandleAsync(domainEvent);

        // Assert — customer email sent
        await _emailService.Received(2).SendAsync(
            Arg.Any<EmailMessage>(),
            Arg.Any<CancellationToken>());

        // One for seller, one for customer
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "payer@example.com" && m.Subject.Contains("Comprovante")),
            Arg.Any<CancellationToken>());

        await _receiptRepo.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task HandleAsync_WhenCaptured_SkipsCustomerEmailIfAlreadySent()
    {
        // Arrange
        var tx = BuildCapturedTransaction(payerEmail: "payer@example.com", payerName: "John Doe");
        _transactionRepo.GetByIdAsync(TenantId, TransactionId).Returns(tx);

        var receipt = Receipt.Create(TenantId, SellerId, ReceiptType.PAYMENT, PaymentProvider.STRIPE, 100m, transactionId: TransactionId);
        receipt.MarkCustomerEmailSent(); // Already sent
        _receiptService.GenerateForPaymentAsync(TenantId, TransactionId).Returns(receipt);

        var tenant = Tenant.Create("Test Tenant", "test-tenant", "hash", "fp_", "secret_hash", "owner@test.com");
        _tenantRepo.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var seller = Seller.Create(TenantId, "Seller LTDA", "12345678000100", "seller@test.com", "whsec_test");
        _sellerRepo.GetByIdAsync(TenantId, SellerId).Returns(seller);

        var domainEvent = BuildCapturedEvent();

        // Act
        await _sut.HandleAsync(domainEvent);

        // Assert — only seller email, NOT customer (already sent)
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "seller@test.com"),
            Arg.Any<CancellationToken>());

        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "payer@example.com"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenCaptured_SkipsCustomerEmailIfNoPayerEmail()
    {
        // Arrange — no payer email on transaction
        var tx = BuildCapturedTransaction(payerEmail: null, payerName: null);
        _transactionRepo.GetByIdAsync(TenantId, TransactionId).Returns(tx);

        var tenant = Tenant.Create("Test Tenant", "test-tenant", "hash", "fp_", "secret_hash", "owner@test.com");
        _tenantRepo.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var seller = Seller.Create(TenantId, "Seller LTDA", "12345678000100", "seller@test.com", "whsec_test");
        _sellerRepo.GetByIdAsync(TenantId, SellerId).Returns(seller);

        var domainEvent = BuildCapturedEvent();

        // Act
        await _sut.HandleAsync(domainEvent);

        // Assert — receipt service not called, no customer email
        await _receiptService.DidNotReceive().GenerateForPaymentAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task HandleAsync_WhenCaptured_FallsBackToCustomerEntityForEmail()
    {
        // Arrange — no PayerEmail on tx, but has CustomerId
        var tx = BuildCapturedTransaction(payerEmail: null, payerName: null, customerId: Guid.NewGuid());
        _transactionRepo.GetByIdAsync(TenantId, TransactionId).Returns(tx);

        var customer = Customer.Create(TenantId, "Jane Customer", "jane@customer.com", "11111111111");
        _customerRepo.GetByIdAsync(TenantId, tx.CustomerId!.Value).Returns(customer);

        var receipt = Receipt.Create(TenantId, SellerId, ReceiptType.PAYMENT, PaymentProvider.STRIPE, 100m, transactionId: TransactionId);
        _receiptService.GenerateForPaymentAsync(TenantId, TransactionId).Returns(receipt);

        var tenant = Tenant.Create("Test Tenant", "test-tenant", "hash", "fp_", "secret_hash", "owner@test.com");
        _tenantRepo.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var seller = Seller.Create(TenantId, "Seller LTDA", "12345678000100", "seller@test.com", "whsec_test");
        _sellerRepo.GetByIdAsync(TenantId, SellerId).Returns(seller);

        var domainEvent = BuildCapturedEvent();

        // Act
        await _sut.HandleAsync(domainEvent);

        // Assert — customer email sent using Customer entity's email
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "jane@customer.com"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenCaptured_RecordsFailureOnReceiptIfEmailFails()
    {
        // Arrange
        var tx = BuildCapturedTransaction(payerEmail: "payer@example.com", payerName: "John Doe");
        _transactionRepo.GetByIdAsync(TenantId, TransactionId).Returns(tx);

        var receipt = Receipt.Create(TenantId, SellerId, ReceiptType.PAYMENT, PaymentProvider.STRIPE, 100m, transactionId: TransactionId);
        _receiptService.GenerateForPaymentAsync(TenantId, TransactionId).Returns(receipt);

        var tenant = Tenant.Create("Test Tenant", "test-tenant", "hash", "fp_", "secret_hash", "owner@test.com");
        _tenantRepo.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var seller = Seller.Create(TenantId, "Seller LTDA", "12345678000100", "seller@test.com", "whsec_test");
        _sellerRepo.GetByIdAsync(TenantId, SellerId).Returns(seller);

        // Make the customer email send fail (second call — first is seller email which succeeds)
        _emailService.SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "payer@example.com"),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("SMTP connection failed"));

        _receiptRepo.GetByTransactionIdAsync(TenantId, TransactionId, ReceiptType.PAYMENT).Returns(receipt);

        var domainEvent = BuildCapturedEvent();

        // Act
        await _sut.HandleAsync(domainEvent);

        // Assert — failure recorded on receipt
        receipt.CustomerEmailLastError.Should().Contain("SMTP connection failed");
        receipt.CustomerEmailAttempts.Should().Be(1);
        receipt.IsCustomerEmailSent.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_WhenNotCaptured_DoesNotSendCustomerEmail()
    {
        // Arrange — status is DECLINED, not CAPTURED
        var domainEvent = new TransactionStatusChangedEvent(
            TransactionId, TenantId,
            TransactionStatus.PROCESSING, TransactionStatus.DECLINED,
            SellerId, 98m, PaymentType.CREDIT_CARD, "pi_123");

        // Act
        await _sut.HandleAsync(domainEvent);

        // Assert — receipt service never called
        await _receiptService.DidNotReceive().GenerateForPaymentAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
        await _transactionRepo.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Fact]
    public async Task HandleAsync_WhenCaptured_SellerEmailStillSent()
    {
        // Arrange — verify seller email is always sent (existing behavior preserved)
        var tx = BuildCapturedTransaction(payerEmail: "payer@example.com", payerName: "John Doe");
        _transactionRepo.GetByIdAsync(TenantId, TransactionId).Returns(tx);

        var receipt = Receipt.Create(TenantId, SellerId, ReceiptType.PAYMENT, PaymentProvider.STRIPE, 100m, transactionId: TransactionId);
        _receiptService.GenerateForPaymentAsync(TenantId, TransactionId).Returns(receipt);

        var tenant = Tenant.Create("Test Tenant", "test-tenant", "hash", "fp_", "secret_hash", "owner@test.com");
        _tenantRepo.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var seller = Seller.Create(TenantId, "Seller LTDA", "12345678000100", "seller@test.com", "whsec_test");
        _sellerRepo.GetByIdAsync(TenantId, SellerId).Returns(seller);

        var domainEvent = BuildCapturedEvent();

        // Act
        await _sut.HandleAsync(domainEvent);

        // Assert — seller email sent (existing behavior)
        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "seller@test.com" && m.Subject.Contains("Pagamento confirmado")),
            Arg.Any<CancellationToken>());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static TransactionStatusChangedEvent BuildCapturedEvent() =>
        new(TransactionId, TenantId,
            TransactionStatus.PROCESSING, TransactionStatus.CAPTURED,
            SellerId, 98m, PaymentType.CREDIT_CARD, "pi_123");

    private static Transaction BuildCapturedTransaction(string? payerEmail, string? payerName, Guid? customerId = null)
    {
        var result = Transaction.Create(
            tenantId: TenantId,
            amount: 100m,
            paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 2m,
            netAmount: 98m,
            expectedSettlementDate: null,
            providerTxId: "pi_123",
            sellerId: SellerId,
            customerId: customerId);

        var tx = result.Value;
        tx.SetPayerInfo(payerEmail, payerName);
        tx.UpdateStatus(TransactionStatus.CAPTURED);
        return tx;
    }
}
