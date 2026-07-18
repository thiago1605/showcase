using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Modules.Dashboard.DTOs;
using FellowCore.Application.Modules.Dashboard.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Tests.Services;

public class DashboardServiceTests
{
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly IReconciliationRepository _reconciliationRepository = Substitute.For<IReconciliationRepository>();
    private readonly IPayoutRepository _payoutRepository = Substitute.For<IPayoutRepository>();
    private readonly IWebhookDeliveryRepository _webhookDeliveryRepository = Substitute.For<IWebhookDeliveryRepository>();
    private readonly IDisputeRepository _disputeRepository = Substitute.For<IDisputeRepository>();
    private readonly DashboardService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();

    public DashboardServiceTests()
    {
        _sut = new DashboardService(
            _transactionRepository,
            _reconciliationRepository,
            _payoutRepository,
            _webhookDeliveryRepository,
            _disputeRepository,
            Substitute.For<ISplitTransferRepository>(),
            Substitute.For<IPaymentLinkRepository>());
    }

    #region GetSummaryAsync

    [Fact]
    public async Task GetSummaryAsync_ShouldReturnZeros_WhenNoTransactions()
    {
        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, null, null, null, null)
            .Returns(new List<Transaction>());

        var filter = new DashboardFilterDto();
        var result = await _sut.GetSummaryAsync(TenantId, filter);

        result.TotalVolume.Should().Be(0);
        result.TotalFees.Should().Be(0);
        result.TotalNet.Should().Be(0);
        result.TransactionCount.Should().Be(0);
        result.ByStatus.Should().BeEmpty();
        result.ByPaymentType.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldAggregateTotals()
    {
        var tx1 = BuildTransaction(100m, 2m, 98m, TransactionStatus.CAPTURED, PaymentType.PIX);
        var tx2 = BuildTransaction(200m, 4m, 196m, TransactionStatus.CAPTURED, PaymentType.CREDIT_CARD);
        var tx3 = BuildTransaction(50m, 1m, 49m, TransactionStatus.PROCESSING, PaymentType.PIX);

        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), null, null)
            .Returns(new List<Transaction> { tx1, tx2, tx3 });

        var filter = new DashboardFilterDto();
        var result = await _sut.GetSummaryAsync(TenantId, filter);

        result.TotalVolume.Should().Be(350m);
        result.TotalFees.Should().Be(7m);
        result.TotalNet.Should().Be(343m);
        result.TransactionCount.Should().Be(3);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldGroupByStatus()
    {
        var tx1 = BuildTransaction(100m, 2m, 98m, TransactionStatus.CAPTURED, PaymentType.PIX);
        var tx2 = BuildTransaction(200m, 4m, 196m, TransactionStatus.CAPTURED, PaymentType.CREDIT_CARD);
        var tx3 = BuildTransaction(50m, 1m, 49m, TransactionStatus.PROCESSING, PaymentType.PIX);

        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), null, null)
            .Returns(new List<Transaction> { tx1, tx2, tx3 });

        var result = await _sut.GetSummaryAsync(TenantId, new DashboardFilterDto());

        result.ByStatus.Should().HaveCount(2);
        var captured = result.ByStatus.First(s => s.Status == TransactionStatus.CAPTURED);
        captured.Count.Should().Be(2);
        captured.Volume.Should().Be(300m);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldGroupByPaymentType()
    {
        var tx1 = BuildTransaction(100m, 2m, 98m, TransactionStatus.CAPTURED, PaymentType.PIX);
        var tx2 = BuildTransaction(200m, 4m, 196m, TransactionStatus.CAPTURED, PaymentType.CREDIT_CARD);
        var tx3 = BuildTransaction(50m, 1m, 49m, TransactionStatus.CAPTURED, PaymentType.PIX);

        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), null, null)
            .Returns(new List<Transaction> { tx1, tx2, tx3 });

        var result = await _sut.GetSummaryAsync(TenantId, new DashboardFilterDto());

        result.ByPaymentType.Should().HaveCount(2);
        var pix = result.ByPaymentType.First(p => p.PaymentType == PaymentType.PIX);
        pix.Count.Should().Be(2);
        pix.Volume.Should().Be(150m);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldPassFilterParametersToRepository()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 31, 23, 59, 59, DateTimeKind.Utc);
        var sellerId = Guid.NewGuid();

        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, from, to, sellerId, PaymentProvider.STRIPE)
            .Returns(new List<Transaction>());

        var filter = new DashboardFilterDto(From: from, To: to, SellerId: sellerId, Provider: PaymentProvider.STRIPE);
        await _sut.GetSummaryAsync(TenantId, filter);

        await _transactionRepository.Received(1)
            .GetByTenantAndDateRangeAsync(TenantId, from, to, sellerId, PaymentProvider.STRIPE);
    }

    [Fact]
    public async Task GetSummaryAsync_ShouldHandleNullFees()
    {
        // Transaction with null FeeAmount and NetAmount
        var tx = BuildTransaction(100m, null, null, TransactionStatus.PROCESSING, PaymentType.BOLETO);

        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), null, null)
            .Returns(new List<Transaction> { tx });

        var result = await _sut.GetSummaryAsync(TenantId, new DashboardFilterDto());

        result.TotalVolume.Should().Be(100m);
        result.TotalFees.Should().Be(0m);
        result.TotalNet.Should().Be(0m);
    }

    #endregion

    #region GetFinancialHealthAsync

    [Fact]
    public async Task GetFinancialHealthAsync_ShouldReturnAllMetrics()
    {
        var latestRun = ReconciliationRun.Create(TenantId, "BATCH", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        latestRun.Complete(100, 20, 50, 3, 0, 150);

        _reconciliationRepository.GetLatestRunAsync(TenantId, "BATCH").Returns(latestRun);
        _payoutRepository.GetPendingSummaryAsync(TenantId).Returns((5, 2500m));
        _webhookDeliveryRepository.GetFailedCountSinceAsync(TenantId, Arg.Any<DateTime>()).Returns(12);
        _disputeRepository.GetOpenDisputeSummaryAsync(TenantId).Returns((2, 500m));

        var result = await _sut.GetFinancialHealthAsync(TenantId);

        result.LedgerImbalanceCount.Should().Be(3);
        result.PlatformDriftCents.Should().Be(150);
        result.LastReconciliationStatus.Should().Be("PASSED");
        result.PendingPayoutsCount.Should().Be(5);
        result.PendingPayoutsTotal.Should().Be(2500m);
        result.FailedWebhooksLast24h.Should().Be(12);
        result.OpenDisputesCount.Should().Be(2);
        result.DisputeExposureAmount.Should().Be(500m);
    }

    [Fact]
    public async Task GetFinancialHealthAsync_ShouldHandleNullReconciliationRun()
    {
        _reconciliationRepository.GetLatestRunAsync(TenantId, "BATCH").Returns((ReconciliationRun?)null);
        _payoutRepository.GetPendingSummaryAsync(TenantId).Returns((0, 0m));
        _webhookDeliveryRepository.GetFailedCountSinceAsync(TenantId, Arg.Any<DateTime>()).Returns(0);
        _disputeRepository.GetOpenDisputeSummaryAsync(TenantId).Returns((0, 0m));

        var result = await _sut.GetFinancialHealthAsync(TenantId);

        result.LedgerImbalanceCount.Should().Be(0);
        result.PlatformDriftCents.Should().BeNull();
        result.LastReconciliationStatus.Should().BeNull();
    }

    [Fact]
    public async Task GetFinancialHealthAsync_ShouldReturnHealthyState_WhenNoIssues()
    {
        var latestRun = ReconciliationRun.Create(TenantId, "BATCH", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        latestRun.Complete(50, 10, 25, 0, 0, 0);

        _reconciliationRepository.GetLatestRunAsync(TenantId, "BATCH").Returns(latestRun);
        _payoutRepository.GetPendingSummaryAsync(TenantId).Returns((0, 0m));
        _webhookDeliveryRepository.GetFailedCountSinceAsync(TenantId, Arg.Any<DateTime>()).Returns(0);
        _disputeRepository.GetOpenDisputeSummaryAsync(TenantId).Returns((0, 0m));

        var result = await _sut.GetFinancialHealthAsync(TenantId);

        result.LedgerImbalanceCount.Should().Be(0);
        result.PlatformDriftCents.Should().Be(0);
        result.LastReconciliationStatus.Should().Be("PASSED");
        result.PendingPayoutsCount.Should().Be(0);
        result.FailedWebhooksLast24h.Should().Be(0);
        result.OpenDisputesCount.Should().Be(0);
        result.DisputeExposureAmount.Should().Be(0m);
    }

    #endregion

    #region Tenant Isolation

    [Fact]
    public async Task GetSummaryAsync_ShouldOnlyQueryForSpecifiedTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        _transactionRepository.GetByTenantAndDateRangeAsync(tenantA, null, null, null, null)
            .Returns(new List<Transaction>());

        await _sut.GetSummaryAsync(tenantA, new DashboardFilterDto());

        await _transactionRepository.Received(1).GetByTenantAndDateRangeAsync(tenantA, null, null, null, null);
        await _transactionRepository.DidNotReceive().GetByTenantAndDateRangeAsync(tenantB, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>());
    }

    [Fact]
    public async Task GetFinancialHealthAsync_ShouldOnlyQueryForSpecifiedTenant()
    {
        var tenantA = Guid.NewGuid();

        _reconciliationRepository.GetLatestRunAsync(tenantA, "BATCH").Returns((ReconciliationRun?)null);
        _payoutRepository.GetPendingSummaryAsync(tenantA).Returns((0, 0m));
        _webhookDeliveryRepository.GetFailedCountSinceAsync(tenantA, Arg.Any<DateTime>()).Returns(0);
        _disputeRepository.GetOpenDisputeSummaryAsync(tenantA).Returns((0, 0m));

        await _sut.GetFinancialHealthAsync(tenantA);

        await _reconciliationRepository.Received(1).GetLatestRunAsync(tenantA, "BATCH");
        await _payoutRepository.Received(1).GetPendingSummaryAsync(tenantA);
        await _disputeRepository.Received(1).GetOpenDisputeSummaryAsync(tenantA);
    }

    #endregion

    #region Helpers

    private static Transaction BuildTransaction(
        decimal amount,
        decimal? feeAmount,
        decimal? netAmount,
        TransactionStatus status,
        PaymentType paymentType,
        string? payerEmail = null)
    {
        var result = Transaction.Create(
            tenantId: TenantId,
            amount: amount,
            paymentType: paymentType,
            provider: PaymentProvider.OPENPIX,
            installments: 1,
            feeAmount: feeAmount,
            netAmount: netAmount,
            expectedSettlementDate: null,
            providerTxId: $"tx_{Guid.NewGuid():N}");

        var tx = result.Value;
        tx.UpdateStatus(status);
        if (payerEmail != null)
            tx.SetPayerInfo(payerEmail, name: null);
        return tx;
    }

    #endregion

    #region GetCustomerRetentionAsync

    [Fact]
    public async Task GetCustomerRetentionAsync_ShouldReturnZero_WhenNoTransactions()
    {
        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), null, null)
            .Returns(new List<Transaction>());

        var result = await _sut.GetCustomerRetentionAsync(TenantId, new DashboardFilterDto());

        result.UniqueCustomers.Should().Be(0);
        result.NewCustomers.Should().Be(0);
        result.ReturningCustomers.Should().Be(0);
        result.RepeatInPeriod.Should().Be(0);
        result.ReturningRate.Should().Be(0);
    }

    [Fact]
    public async Task GetCustomerRetentionAsync_ShouldIgnoreNonCapturedAndAnonymous()
    {
        // Não-capturadas e sem email não entram no count.
        var declined = BuildTransaction(100m, null, null, TransactionStatus.DECLINED, PaymentType.PIX, payerEmail: "rejected@example.com");
        var anonymous = BuildTransaction(100m, null, null, TransactionStatus.CAPTURED, PaymentType.PIX, payerEmail: null);
        var capturedKnown = BuildTransaction(100m, null, null, TransactionStatus.CAPTURED, PaymentType.PIX, payerEmail: "alice@example.com");

        var from = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var filter = new DashboardFilterDto(from, to, null, null);

        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, from, to, null, null)
            .Returns(new List<Transaction> { declined, anonymous, capturedKnown });
        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, Arg.Is<DateTime?>(d => d != from), Arg.Is<DateTime?>(d => d != to), null, null)
            .Returns(new List<Transaction>());

        var result = await _sut.GetCustomerRetentionAsync(TenantId, filter);

        result.UniqueCustomers.Should().Be(1); // só "alice@example.com"
        result.NewCustomers.Should().Be(1);
        result.ReturningCustomers.Should().Be(0);
    }

    [Fact]
    public async Task GetCustomerRetentionAsync_ShouldClassifyReturningFromHistory()
    {
        // Cliente comprou antes do período (histórico) → returning. Outro só no período → new.
        var from = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var filter = new DashboardFilterDto(from, to, null, null);

        var aliceInPeriod = BuildTransaction(100m, null, null, TransactionStatus.CAPTURED, PaymentType.PIX, payerEmail: "alice@example.com");
        var bobInPeriod = BuildTransaction(200m, null, null, TransactionStatus.CAPTURED, PaymentType.PIX, payerEmail: "bob@example.com");
        var aliceHistoric = BuildTransaction(50m, null, null, TransactionStatus.CAPTURED, PaymentType.PIX, payerEmail: "alice@example.com");

        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, from, to, null, null)
            .Returns(new List<Transaction> { aliceInPeriod, bobInPeriod });
        // Histórico (1 ano antes do `from` até `from`): só Alice.
        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, Arg.Is<DateTime?>(d => d.HasValue && d.Value < from), Arg.Is<DateTime?>(d => d.HasValue && d.Value <= from), null, null)
            .Returns(new List<Transaction> { aliceHistoric });

        var result = await _sut.GetCustomerRetentionAsync(TenantId, filter);

        result.UniqueCustomers.Should().Be(2);
        result.ReturningCustomers.Should().Be(1); // Alice
        result.NewCustomers.Should().Be(1); // Bob
        result.ReturningRate.Should().Be(50);
    }

    [Fact]
    public async Task GetCustomerRetentionAsync_ShouldCountRepeatInPeriod()
    {
        // Alice paga 2x dentro do período → conta como 1 cliente único, mas 1 repeatInPeriod.
        var from = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var filter = new DashboardFilterDto(from, to, null, null);

        var alice1 = BuildTransaction(100m, null, null, TransactionStatus.CAPTURED, PaymentType.PIX, payerEmail: "alice@example.com");
        var alice2 = BuildTransaction(100m, null, null, TransactionStatus.CAPTURED, PaymentType.PIX, payerEmail: "alice@example.com");
        var bob = BuildTransaction(200m, null, null, TransactionStatus.CAPTURED, PaymentType.PIX, payerEmail: "bob@example.com");

        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, from, to, null, null)
            .Returns(new List<Transaction> { alice1, alice2, bob });
        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, Arg.Is<DateTime?>(d => d.HasValue && d.Value < from), Arg.Is<DateTime?>(d => d.HasValue && d.Value <= from), null, null)
            .Returns(new List<Transaction>());

        var result = await _sut.GetCustomerRetentionAsync(TenantId, filter);

        result.UniqueCustomers.Should().Be(2);
        result.RepeatInPeriod.Should().Be(1); // só Alice
        result.RepeatInPeriodRate.Should().Be(50);
    }

    [Fact]
    public async Task GetCustomerRetentionAsync_ShouldNormalizeEmailCase()
    {
        // "Alice@Example.com" e "alice@example.com" devem ser tratados como o mesmo cliente.
        var from = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var filter = new DashboardFilterDto(from, to, null, null);

        var aliceUpper = BuildTransaction(100m, null, null, TransactionStatus.CAPTURED, PaymentType.PIX, payerEmail: "Alice@Example.com");
        var aliceLower = BuildTransaction(100m, null, null, TransactionStatus.CAPTURED, PaymentType.PIX, payerEmail: "alice@example.com");

        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, from, to, null, null)
            .Returns(new List<Transaction> { aliceUpper, aliceLower });
        _transactionRepository.GetByTenantAndDateRangeAsync(TenantId, Arg.Is<DateTime?>(d => d.HasValue && d.Value < from), Arg.Is<DateTime?>(d => d.HasValue && d.Value <= from), null, null)
            .Returns(new List<Transaction>());

        var result = await _sut.GetCustomerRetentionAsync(TenantId, filter);

        result.UniqueCustomers.Should().Be(1);
        result.RepeatInPeriod.Should().Be(1);
    }

    #endregion
}
