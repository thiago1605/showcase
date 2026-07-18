using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Providers;
using FellowCore.Application.Modules.Reconciliation.Services;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Models;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Models;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Domain.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Flows;

public class SettlementReconciliationTests
{
    private readonly ISettlementReportRepository _settlementReportRepo = Substitute.For<ISettlementReportRepository>();
    private readonly ITransactionRepository _transactionRepo = Substitute.For<ITransactionRepository>();
    private readonly IPayoutRepository _payoutRepo = Substitute.For<IPayoutRepository>();
    private readonly IReconciliationRepository _reconciliationRepo = Substitute.For<IReconciliationRepository>();
    private readonly ITenantRepository _tenantRepo = Substitute.For<ITenantRepository>();
    private readonly IAlertService _alertService = Substitute.For<IAlertService>();
    private readonly IStripeApiClient _stripeApiClient = Substitute.For<IStripeApiClient>();
    private readonly IOpenPixApiClient _openPixApiClient = Substitute.For<IOpenPixApiClient>();
    private readonly IConfiguration _configuration;

    private static readonly Guid TenantId = Guid.NewGuid();

    public SettlementReconciliationTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Stripe:SecretKey", "sk_test_fake" },
                { "OpenPix:AppId", "openpix_test_fake" }
            })
            .Build();

        _tenantRepo.GetAllAsync().Returns(new List<Tenant> { CreateTenant() });
        _payoutRepo.GetByTenantAndDateRangeAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<PayoutStatus?>())
            .Returns(new List<Payout>());
    }

    // ── Stripe Settlement Provider Tests ──────────────────────────────

    [Fact]
    public async Task StripeProvider_ImportsBalanceTransactions_MapsCorrectly()
    {
        var provider = CreateStripeProvider();
        var reportId = Guid.NewGuid();

        _stripeApiClient.ListBalanceTransactionsAsync(
            Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeBalanceTransactionListResponse(
                Data: new List<StripeBalanceTransaction>
                {
                    new("txn_001", "charge", Amount: 10000, Fee: 350, Net: 9650, Currency: "brl", Created: 1714435200, Source: "ch_001"),
                    new("txn_002", "refund", Amount: -5000, Fee: -175, Net: -4825, Currency: "brl", Created: 1714521600, Source: "re_001"),
                    new("txn_003", "payout", Amount: -50000, Fee: 0, Net: -50000, Currency: "brl", Created: 1714694400, Source: "po_001"),
                    new("txn_004", "stripe_fee", Amount: -100, Fee: 0, Net: -100, Currency: "brl", Created: 1714435200)
                }));

        var items = await provider.ImportAsync(reportId, "sk_test", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        items.Should().HaveCount(3); // stripe_fee is skipped
        items[0].ItemType.Should().Be(SettlementItemType.CHARGE);
        items[0].GrossAmountCents.Should().Be(10000);
        items[0].FeeAmountCents.Should().Be(350);
        items[0].ChargeId.Should().Be("ch_001");

        items[1].ItemType.Should().Be(SettlementItemType.REFUND);
        items[1].RefundAmountCents.Should().Be(5000);

        items[2].ItemType.Should().Be(SettlementItemType.PAYOUT);
        items[2].PayoutId.Should().Be("po_001");
    }

    [Fact]
    public async Task StripeProvider_EmptyResponse_ReturnsEmptyList()
    {
        var provider = CreateStripeProvider();
        _stripeApiClient.ListBalanceTransactionsAsync(
            Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeBalanceTransactionListResponse());

        var items = await provider.ImportAsync(Guid.NewGuid(), "sk_test", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        items.Should().BeEmpty();
    }

    // ── OpenPix Settlement Provider Tests ─────────────────────────────

    [Fact]
    public async Task OpenPixProvider_ImportsStatement_MapsCorrectly()
    {
        var provider = CreateOpenPixProvider();
        var reportId = Guid.NewGuid();

        _openPixApiClient.GetStatementAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>())
            .Returns(new OpenPixStatementResponse(
                Transactions: new List<OpenPixStatementEntry>
                {
                    new(EndToEndId: "e2e_001", Value: 5000, Time: "2024-04-30T12:00:00Z", Type: "PAYMENT"),
                    new(EndToEndId: "e2e_002", Value: 3000, Time: "2024-04-30T14:00:00Z", Type: "REFUND"),
                    new(EndToEndId: "e2e_003", Value: 10000, Time: "2024-04-30T16:00:00Z", Type: "WITHDRAW"),
                    new(EndToEndId: "e2e_004", Value: 500, Time: "2024-04-30T18:00:00Z", Type: "UNKNOWN")
                }));

        var items = await provider.ImportAsync(reportId, "appid", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        items.Should().HaveCount(3); // UNKNOWN is skipped
        items[0].ItemType.Should().Be(SettlementItemType.CHARGE);
        items[0].GrossAmountCents.Should().Be(5000);
        items[1].ItemType.Should().Be(SettlementItemType.REFUND);
        items[2].ItemType.Should().Be(SettlementItemType.PAYOUT);
    }

    [Fact]
    public async Task OpenPixProvider_EmptyStatement_ReturnsEmptyList()
    {
        var provider = CreateOpenPixProvider();
        _openPixApiClient.GetStatementAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>())
            .Returns(new OpenPixStatementResponse(Transactions: new List<OpenPixStatementEntry>()));

        var items = await provider.ImportAsync(Guid.NewGuid(), "appid", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        items.Should().BeEmpty();
    }

    // ── CSV Settlement Provider Tests ─────────────────────────────────

    [Fact]
    public void CsvProvider_ParsesValidCsv()
    {
        var provider = new CsvSettlementProvider(Substitute.For<ILogger<CsvSettlementProvider>>());

        var csv = """
            provider_tx_id,type,gross_cents,fee_cents,net_cents,currency,date,status
            tx_001,CHARGE,5000,0,5000,BRL,2024-04-30T12:00:00Z,completed
            tx_002,REFUND,-3000,0,-3000,BRL,2024-04-30T14:00:00Z,completed
            tx_003,PAYOUT,-8000,100,-8100,BRL,2024-04-30T18:00:00Z,completed
            """;

        var items = provider.ParseCsv(Guid.NewGuid(), csv);

        items.Should().HaveCount(3);
        items[0].ItemType.Should().Be(SettlementItemType.CHARGE);
        items[0].GrossAmountCents.Should().Be(5000);
        items[1].ItemType.Should().Be(SettlementItemType.REFUND);
        items[2].ItemType.Should().Be(SettlementItemType.PAYOUT);
        items[2].FeeAmountCents.Should().Be(100);
    }

    [Fact]
    public void CsvProvider_EmptyCsv_ReturnsEmpty()
    {
        var provider = new CsvSettlementProvider(Substitute.For<ILogger<CsvSettlementProvider>>());
        var items = provider.ParseCsv(Guid.NewGuid(), "header_only");
        items.Should().BeEmpty();
    }

    [Fact]
    public void CsvProvider_MalformedRows_SkipsInvalid()
    {
        var provider = new CsvSettlementProvider(Substitute.For<ILogger<CsvSettlementProvider>>());

        var csv = """
            provider_tx_id,type,gross_cents,fee_cents,net_cents,currency,date,status
            tx_001,CHARGE,5000,0,5000,BRL,2024-04-30T12:00:00Z,completed
            bad_row,only_two
            tx_002,CHARGE,notanumber,0,0,BRL,2024-04-30T12:00:00Z,completed
            """;

        var items = provider.ParseCsv(Guid.NewGuid(), csv);
        items.Should().HaveCount(1);
    }

    // ── Fake Settlement Provider Tests ────────────────────────────────

    [Fact]
    public async Task FakeProvider_ReturnsConfiguredItems()
    {
        var provider = new FakeSettlementProvider();
        var reportId = Guid.NewGuid();

        var item = SettlementReportItem.Create(
            reportId, "fake_tx_001", SettlementItemType.CHARGE, 10000, 350, 9650,
            DateTime.UtcNow, "available");

        provider.Configure(new[] { item });

        var result = await provider.ImportAsync(reportId, "key", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        result.Should().HaveCount(1);
        result[0].ProviderTransactionId.Should().Be("fake_tx_001");
    }

    // ── Settlement Reconciliation Service Tests ───────────────────────

    [Fact]
    public async Task Reconciliation_AllMatched_ReportStatusReconciled()
    {
        var sut = CreateSettlementReconciliationService(out var fakeProvider);

        var reportId = Guid.NewGuid();
        var tx = CreateTransaction(TenantId, 100m, "pi_001", PaymentProvider.SANDBOX);
        _transactionRepo.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        fakeProvider.Configure(new[]
        {
            SettlementReportItem.Create(reportId, "pi_001", SettlementItemType.CHARGE,
                10000, 350, 9650, DateTime.UtcNow, "available", chargeId: "pi_001")
        });

        var report = await sut.ImportAndReconcileAsync(TenantId, PaymentProvider.SANDBOX, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        report.Status.Should().Be("RECONCILED");
        report.MatchedItems.Should().Be(1);
        report.MismatchedItems.Should().Be(0);
    }

    [Fact]
    public async Task Reconciliation_AmountMismatch_ReportHasIssues()
    {
        var sut = CreateSettlementReconciliationService(out var fakeProvider);

        var tx = CreateTransaction(TenantId, 100m, "pi_002", PaymentProvider.SANDBOX);
        _transactionRepo.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction> { tx });

        fakeProvider.Configure(new[]
        {
            SettlementReportItem.Create(Guid.NewGuid(), "pi_002", SettlementItemType.CHARGE,
                15000, 525, 14475, DateTime.UtcNow, "available", chargeId: "pi_002")
        });

        var report = await sut.ImportAndReconcileAsync(TenantId, PaymentProvider.SANDBOX, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        report.Status.Should().Be("RECONCILED_WITH_ISSUES");
        report.MismatchedItems.Should().Be(1);
    }

    [Fact]
    public async Task Reconciliation_MissingInternal_ReportHasIssues()
    {
        var sut = CreateSettlementReconciliationService(out var fakeProvider);

        _transactionRepo.GetByTenantAndDateRangeAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<PaymentProvider?>())
            .Returns(new List<Transaction>()); // No internal transactions

        fakeProvider.Configure(new[]
        {
            SettlementReportItem.Create(Guid.NewGuid(), "pi_orphan", SettlementItemType.CHARGE,
                10000, 350, 9650, DateTime.UtcNow, "available", chargeId: "pi_orphan")
        });

        var report = await sut.ImportAndReconcileAsync(TenantId, PaymentProvider.SANDBOX, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        report.Status.Should().Be("RECONCILED_WITH_ISSUES");
        report.MissingInternalItems.Should().Be(1);
        _reconciliationRepo.Received().AddRun(Arg.Is<ReconciliationRun>(r => r.TenantId == TenantId));
    }

    [Fact]
    public async Task Reconciliation_ProviderError_ReportMarkedFailed()
    {
        var sut = CreateSettlementReconciliationService(out var fakeProvider);

        // Clear providers — no provider for STRIPE
        var noProviders = Array.Empty<ISettlementReportProvider>();
        var sutNoProvider = new SettlementReconciliationService(
            noProviders, _settlementReportRepo, _transactionRepo, _payoutRepo,
            _reconciliationRepo, _tenantRepo, _alertService, _configuration,
            Substitute.For<ILogger<SettlementReconciliationService>>());

        var report = await sutNoProvider.ImportAndReconcileAsync(TenantId, PaymentProvider.STRIPE, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        report.Status.Should().Be("FAILED");
        report.ErrorMessage.Should().Contain("No settlement provider");
    }

    // ── SettlementReport Entity Tests ─────────────────────────────────

    [Fact]
    public void SettlementReport_Create_SetsDefaults()
    {
        var report = SettlementReport.Create(TenantId, PaymentProvider.STRIPE, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        report.Id.Should().NotBeEmpty();
        report.TenantId.Should().Be(TenantId);
        report.Provider.Should().Be(PaymentProvider.STRIPE);
        report.Status.Should().Be("IMPORTED");
        report.TotalItems.Should().Be(0);
    }

    [Fact]
    public void SettlementReport_MarkReconciled_Clean()
    {
        var report = SettlementReport.Create(TenantId, PaymentProvider.STRIPE, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        report.MarkReconciled(matched: 10, mismatched: 0, missingInternal: 0, missingExternal: 0);

        report.Status.Should().Be("RECONCILED");
        report.TotalItems.Should().Be(10);
        report.MatchedItems.Should().Be(10);
    }

    [Fact]
    public void SettlementReport_MarkReconciled_WithIssues()
    {
        var report = SettlementReport.Create(TenantId, PaymentProvider.STRIPE, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        report.MarkReconciled(matched: 8, mismatched: 1, missingInternal: 1, missingExternal: 0);

        report.Status.Should().Be("RECONCILED_WITH_ISSUES");
        report.TotalItems.Should().Be(10);
    }

    [Fact]
    public void SettlementReport_MarkFailed_TruncatesLongError()
    {
        var report = SettlementReport.Create(TenantId, PaymentProvider.STRIPE, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        var longError = new string('x', 3000);
        report.MarkFailed(longError);

        report.Status.Should().Be("FAILED");
        report.ErrorMessage!.Length.Should().Be(2000);
    }

    // ── SettlementReportItem Entity Tests ─────────────────────────────

    [Fact]
    public void ReportItem_MarkMatched_SetsCorrectState()
    {
        var item = SettlementReportItem.Create(
            Guid.NewGuid(), "txn_001", SettlementItemType.CHARGE, 10000, 350, 9650, DateTime.UtcNow, "available");

        item.MarkMatched("internal_001", 10000);

        item.MatchStatus.Should().Be(SettlementItemMatchStatus.MATCHED);
        item.InternalTransactionId.Should().Be("internal_001");
        item.DriftCents.Should().Be(0);
    }

    [Fact]
    public void ReportItem_MarkMismatched_CalculatesDrift()
    {
        var item = SettlementReportItem.Create(
            Guid.NewGuid(), "txn_001", SettlementItemType.CHARGE, 10000, 350, 9650, DateTime.UtcNow, "available");

        item.MarkMismatched("internal_001", 9500, "Amount differs by 500 cents");

        item.MatchStatus.Should().Be(SettlementItemMatchStatus.MISMATCHED);
        item.DriftCents.Should().Be(500);
        item.MismatchReason.Should().Contain("Amount differs");
    }

    [Fact]
    public void ReportItem_MarkMissingInternal_SetsStatus()
    {
        var item = SettlementReportItem.Create(
            Guid.NewGuid(), "txn_001", SettlementItemType.CHARGE, 10000, 350, 9650, DateTime.UtcNow, "available");

        item.MarkMissingInternal();

        item.MatchStatus.Should().Be(SettlementItemMatchStatus.MISSING_INTERNAL);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private StripeSettlementProvider CreateStripeProvider()
        => new(_stripeApiClient, Substitute.For<ILogger<StripeSettlementProvider>>());

    private OpenPixSettlementProvider CreateOpenPixProvider()
        => new(_openPixApiClient, Substitute.For<ILogger<OpenPixSettlementProvider>>());

    private SettlementReconciliationService CreateSettlementReconciliationService(out FakeSettlementProvider fakeProvider)
    {
        fakeProvider = new FakeSettlementProvider();
        var providers = new ISettlementReportProvider[] { fakeProvider };

        return new SettlementReconciliationService(
            providers, _settlementReportRepo, _transactionRepo, _payoutRepo,
            _reconciliationRepo, _tenantRepo, _alertService, _configuration,
            Substitute.For<ILogger<SettlementReconciliationService>>());
    }

    private static Tenant CreateTenant()
    {
        var tenant = Tenant.Create("Test Tenant", "test-tenant", "hash", "fp_test", "secret_hash");
        typeof(Tenant).BaseType!.BaseType!.GetProperty("Id")!.SetValue(tenant, TenantId);
        return tenant;
    }

    private static Transaction CreateTransaction(Guid tenantId, decimal amount, string providerTxId, PaymentProvider provider)
    {
        var result = Transaction.Create(
            tenantId, amount, PaymentType.CREDIT_CARD, provider,
            installments: 1, feeAmount: 0, netAmount: amount,
            expectedSettlementDate: null, providerTxId: providerTxId);
        var tx = result.Value;
        typeof(Transaction).GetProperty("Status")!.SetValue(tx, TransactionStatus.CAPTURED);
        return tx;
    }
}
