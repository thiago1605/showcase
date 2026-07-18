using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Receipts;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class ReceiptServiceTests
{
    private readonly IReceiptRepository _receiptRepository = Substitute.For<IReceiptRepository>();
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly IPayoutRepository _payoutRepository = Substitute.For<IPayoutRepository>();
    private readonly IRefundIntentRepository _refundIntentRepository = Substitute.For<IRefundIntentRepository>();
    private readonly ReceiptService _sut;

    public ReceiptServiceTests()
    {
        _sut = new ReceiptService(
            _receiptRepository,
            _transactionRepository,
            _payoutRepository,
            _refundIntentRepository,
            Substitute.For<ILogger<ReceiptService>>());
    }

    [Fact]
    public async Task GenerateForPaymentAsync_ValidTransaction_CreatesReceipt()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var transaction = BuildCapturedTransaction(tenantId, sellerId, transactionId);

        _receiptRepository.GetByTransactionIdAsync(tenantId, transactionId, ReceiptType.PAYMENT).Returns((Receipt?)null);
        _transactionRepository.GetByIdAsync(tenantId, transactionId).Returns(transaction);

        var result = await _sut.GenerateForPaymentAsync(tenantId, transactionId);

        result.Should().NotBeNull();
        result.TransactionId.Should().Be(transactionId);
        result.Type.Should().Be(ReceiptType.PAYMENT);
        result.Amount.Should().Be(100m);
        _receiptRepository.Received(1).Add(Arg.Any<Receipt>());
        await _receiptRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task GenerateForPaymentAsync_AlreadyExists_ReturnsExisting()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var existing = Receipt.Create(tenantId, sellerId, ReceiptType.PAYMENT, PaymentProvider.STRIPE, 100m,
            transactionId: transactionId);

        _receiptRepository.GetByTransactionIdAsync(tenantId, transactionId, ReceiptType.PAYMENT).Returns(existing);

        var result = await _sut.GenerateForPaymentAsync(tenantId, transactionId);

        result.Should().Be(existing);
        _receiptRepository.DidNotReceive().Add(Arg.Any<Receipt>());
    }

    [Fact]
    public async Task GenerateForPaymentAsync_TransactionNotFound_Throws()
    {
        var tenantId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        _receiptRepository.GetByTransactionIdAsync(tenantId, transactionId, ReceiptType.PAYMENT).Returns((Receipt?)null);
        _transactionRepository.GetByIdAsync(tenantId, transactionId).Returns((Transaction?)null);

        var act = () => _sut.GenerateForPaymentAsync(tenantId, transactionId);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GenerateForPayoutAsync_ValidPayout_CreatesReceipt()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var payoutId = Guid.NewGuid();
        var payout = Payout.Create(tenantId, sellerId, 500m, 10m).Value;
        payout.Complete("bank-tx-123");
        typeof(Payout).GetProperty("Id")!.SetValue(payout, payoutId);

        _receiptRepository.ExistsAsync(tenantId, null, payoutId, null, ReceiptType.PAYOUT).Returns(false);
        _payoutRepository.GetByIdAsync(tenantId, payoutId).Returns(payout);

        var result = await _sut.GenerateForPayoutAsync(tenantId, payoutId);

        result.Should().NotBeNull();
        result.Type.Should().Be(ReceiptType.PAYOUT);
        result.Amount.Should().Be(490m); // 500 - 10 fee
    }

    [Fact]
    public async Task GenerateForRefundAsync_ValidRefund_CreatesReceipt()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var refund = RefundIntent.Create(tenantId, transactionId, 50m, "Teste");
        typeof(RefundIntent).GetProperty("Id")!.SetValue(refund, refundId);
        refund.MarkProcessing();
        refund.Complete("prov-refund-1");
        var transaction = BuildCapturedTransaction(tenantId, sellerId, transactionId);

        _receiptRepository.GetByRefundIntentIdAsync(tenantId, refundId).Returns((Receipt?)null);
        _refundIntentRepository.GetByIdAsync(refundId).Returns(refund);
        _transactionRepository.GetByIdAsync(tenantId, transactionId).Returns(transaction);

        var result = await _sut.GenerateForRefundAsync(tenantId, refundId);

        result.Should().NotBeNull();
        result.Type.Should().Be(ReceiptType.REFUND);
        result.Amount.Should().Be(50m);
    }

    [Fact]
    public async Task GetBySellerAsync_ReturnsReceipts()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var receipts = new List<Receipt>
        {
            Receipt.Create(tenantId, sellerId, ReceiptType.PAYMENT, PaymentProvider.STRIPE, 100m)
        };

        _receiptRepository.GetBySellerAsync(tenantId, sellerId, 50, 0).Returns(receipts);

        var result = await _sut.GetBySellerAsync(tenantId, sellerId);

        result.Should().HaveCount(1);
    }

    private static Transaction BuildCapturedTransaction(Guid tenantId, Guid sellerId, Guid transactionId)
    {
        var result = Transaction.Create(
            tenantId: tenantId,
            amount: 100m,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 2m,
            netAmount: 98m,
            expectedSettlementDate: null,
            providerTxId: "prov-1",
            sellerId: sellerId);
        var tx = result.Value;
        tx.UpdateStatus(TransactionStatus.CAPTURED);
        typeof(Transaction).GetProperty("Id")!.SetValue(tx, transactionId);
        return tx;
    }
}
