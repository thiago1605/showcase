using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Processors;

public class DunningProcessorTests
{
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly IPaymentProviderFactory _providerFactory = Substitute.For<IPaymentProviderFactory>();
    private readonly IPaymentProvider _paymentProvider = Substitute.For<IPaymentProvider>();
    private readonly ILogger<DunningProcessor> _logger = Substitute.For<ILogger<DunningProcessor>>();
    private readonly DunningProcessor _sut;

    public DunningProcessorTests()
    {
        _providerFactory.GetProvider(Arg.Any<PaymentProvider>()).Returns(_paymentProvider);
        _sut = new DunningProcessor(_transactionRepository, _providerFactory, _logger);
    }

    [Fact]
    public async Task ProcessDunningAsync_ShouldDoNothing_WhenNoEligibleTransactions()
    {
        // Arrange
        _transactionRepository.GetDunningEligibleAsync(Arg.Any<DateTime>())
            .Returns(new List<Transaction>());

        // Act
        await _sut.ProcessDunningAsync();

        // Assert
        _providerFactory.DidNotReceive().GetProvider(Arg.Any<PaymentProvider>());
        await _transactionRepository.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessDunningAsync_ShouldRetryPaymentAndUpdateStatus_WhenProviderSucceeds()
    {
        // Arrange
        var tx = CreateDeclinedTransaction();

        _transactionRepository.GetDunningEligibleAsync(Arg.Any<DateTime>())
            .Returns(new List<Transaction> { tx });

        _paymentProvider.ProcessPaymentAsync(
                Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<CreateTransactionDto>(), Arg.Any<decimal>(), Arg.Any<string?>())
            .Returns(new GatewayPaymentDetails("prov_retry_001"));

        // Act
        await _sut.ProcessDunningAsync();

        // Assert
        tx.DunningAttempts.Should().Be(1);
        tx.Status.Should().Be(TransactionStatus.PROCESSING);

        _transactionRepository.Received(1).Update(tx);
        await _transactionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessDunningAsync_ShouldRecordFailedAttempt_WhenProviderThrows()
    {
        // Arrange
        var tx = CreateDeclinedTransaction();
        var originalDunningAttempts = tx.DunningAttempts;

        _transactionRepository.GetDunningEligibleAsync(Arg.Any<DateTime>())
            .Returns(new List<Transaction> { tx });

        _paymentProvider.ProcessPaymentAsync(
                Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<CreateTransactionDto>(), Arg.Any<decimal>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("Provider unavailable"));

        // Act
        await _sut.ProcessDunningAsync();

        // Assert
        tx.DunningAttempts.Should().Be(originalDunningAttempts + 1);
        // Status should remain DECLINED because retry failed
        tx.Status.Should().Be(TransactionStatus.DECLINED);

        _transactionRepository.Received(1).Update(tx);
        await _transactionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessDunningAsync_ShouldProcessMultipleTransactions()
    {
        // Arrange
        var tx1 = CreateDeclinedTransaction();
        var tx2 = CreateDeclinedTransaction();

        _transactionRepository.GetDunningEligibleAsync(Arg.Any<DateTime>())
            .Returns(new List<Transaction> { tx1, tx2 });

        _paymentProvider.ProcessPaymentAsync(
                Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<CreateTransactionDto>(), Arg.Any<decimal>(), Arg.Any<string?>())
            .Returns(new GatewayPaymentDetails("prov_retry"));

        // Act
        await _sut.ProcessDunningAsync();

        // Assert
        tx1.DunningAttempts.Should().Be(1);
        tx2.DunningAttempts.Should().Be(1);
        _transactionRepository.Received(1).Update(tx1);
        _transactionRepository.Received(1).Update(tx2);
        await _transactionRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task ProcessDunningAsync_ShouldRespectCancellationToken()
    {
        // Arrange
        var tx1 = CreateDeclinedTransaction();
        var tx2 = CreateDeclinedTransaction();

        _transactionRepository.GetDunningEligibleAsync(Arg.Any<DateTime>())
            .Returns(new List<Transaction> { tx1, tx2 });

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        await _sut.ProcessDunningAsync(cts.Token);

        // Assert — cancellation checked before first iteration
        _providerFactory.DidNotReceive().GetProvider(Arg.Any<PaymentProvider>());
    }

    [Fact]
    public async Task ProcessDunningAsync_ShouldCallProviderWithCorrectParameters()
    {
        // Arrange
        var tx = CreateDeclinedTransaction();

        _transactionRepository.GetDunningEligibleAsync(Arg.Any<DateTime>())
            .Returns(new List<Transaction> { tx });

        _paymentProvider.ProcessPaymentAsync(
                Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<CreateTransactionDto>(), Arg.Any<decimal>(), Arg.Any<string?>())
            .Returns(new GatewayPaymentDetails("prov_retry"));

        // Act
        await _sut.ProcessDunningAsync();

        // Assert
        _providerFactory.Received(1).GetProvider(tx.Provider);

        await _paymentProvider.Received(1).ProcessPaymentAsync(
            tx.Tenant,
            tx.Seller,
            Arg.Is<CreateTransactionDto>(dto =>
                dto.Amount == tx.Amount &&
                dto.PaymentType == tx.PaymentType &&
                dto.Installments == tx.Installments &&
                dto.Description!.Contains("Dunning retry")),
            tx.FeeAmount ?? 0,
            Arg.Any<string?>());
    }

    #region Helpers

    private static Transaction CreateDeclinedTransaction()
    {
        var result = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: 100m,
            paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 3m,
            netAmount: 97m,
            expectedSettlementDate: null,
            providerTxId: "prov_tx_001");

        var tx = result.Value;
        // Move to DECLINED to trigger dunning
        tx.UpdateStatus(TransactionStatus.DECLINED);
        return tx;
    }

    #endregion
}
