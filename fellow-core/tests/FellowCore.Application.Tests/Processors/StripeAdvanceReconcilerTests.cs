using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Models;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using FellowCore.Infrastructure.Repositories;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FellowCore.Application.Tests.Processors;

/// <summary>
/// Cobre <see cref="StripeAdvanceReconciler"/> — reconciler baseado em
/// Stripe balance_transactions. AppDbContext in-memory + IStripeApiClient mockado.
///
/// Nota EF in-memory: o provider InMemory tem suporte parcial pra
/// <c>ComplexProperty</c> (usado em <see cref="Seller.Fees"/>). Quando o reconciler
/// chama <c>SellerRepository.GetByIdAsync</c>, a materialização do Seller falha com
/// KeyNotFoundException ("Property: Seller.Fees#FeeSchedule.FeeCreditCash"). A solução
/// é mockar <see cref="ISellerRepository.GetByIdAsync"/> pra retornar a entidade
/// in-memory direto (sem reading via LINQ→provider), e roteamos Update + SaveChanges
/// pro contexto real.
/// </summary>
public class StripeAdvanceReconcilerTests
{
    private static AppDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"StripeRecon_{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(opts);
    }

    private static IConfiguration BuildConfig(bool useStripe = true)
    {
        var dict = new Dictionary<string, string?>
        {
            ["AdvanceReconciler:UseStripe"] = useStripe.ToString(),
            ["Stripe:SecretKey"] = "sk_test_xxx",
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
    }

    private static (Tenant tenant, TenantConfig config, Seller seller) SeedTenant(AppDbContext ctx)
    {
        var tenant = Tenant.Create("Test", $"slug-{Guid.NewGuid():N}", "hash", "pk_test_xx", "secret");
        var config = TenantConfig.Create(tenant.Id);
        tenant.AttachConfig(config);
        ctx.Tenants.Add(tenant);
        ctx.TenantConfigs.Add(config);

        var seller = Seller.Create(tenant.Id, "X", "12345678901", "x@x.com", "ws",
            PaymentProvider.STRIPE, "acct_x", "enc");
        seller.SetAdvanceCreditLimit(10_000m);
        ctx.Sellers.Add(seller);

        ctx.SaveChanges();
        return (tenant, config, seller);
    }

    private static Transaction SeedAdvanceTx(AppDbContext ctx, Guid tenantId, Guid sellerId,
        string chargeId, decimal netAmount = 561m, int installments = 6,
        decimal advanceFee = 19.64m)
    {
        var tx = Transaction.Create(
            tenantId: tenantId, amount: 600m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: installments,
            feeAmount: 600m - netAmount, netAmount: netAmount,
            expectedSettlementDate: null, providerTxId: $"pi_{Guid.NewGuid():N}",
            sellerId: sellerId).Value;
        typeof(Transaction).GetProperty("Status")!.SetValue(tx, TransactionStatus.CAPTURED);
        tx.MarkAsAdvanceSettlement(advanceFee);
        tx.SetStripeChargeId(chargeId);
        ctx.Transactions.Add(tx);
        ctx.SaveChanges();
        return tx;
    }

    /// <summary>
    /// Mock que retorna a entidade in-memory direto — evita materialização via EF
    /// in-memory que choka com ComplexProperty (Seller.Fees).
    /// </summary>
    private static ISellerRepository BuildSellerRepo(Seller seller)
    {
        var repo = Substitute.For<ISellerRepository>();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(callInfo =>
            {
                var sId = (Guid)callInfo[1];
                return Task.FromResult<Seller?>(sId == seller.Id ? seller : null);
            });
        return repo;
    }

    [Fact]
    public async Task ProcessAsync_DisabledViaConfig_NoOp()
    {
        using var ctx = CreateContext();
        var stripeApi = Substitute.For<IStripeApiClient>();
        var tenantRepo = Substitute.For<ITenantRepository>();
        var sellerRepo = Substitute.For<ISellerRepository>();
        var sut = new StripeAdvanceReconciler(ctx, tenantRepo, sellerRepo, stripeApi,
            BuildConfig(useStripe: false), NullLogger<StripeAdvanceReconciler>.Instance);

        await sut.ProcessAsync();

        await tenantRepo.DidNotReceive().GetAllAsync();
        await stripeApi.DidNotReceive().ListBalanceTransactionsAsync(
            Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ProcessAsync_NoBalanceTransactions_AdvancesCursorAndNoOps()
    {
        using var ctx = CreateContext();
        var (tenant, _, seller) = SeedTenant(ctx);
        var tenantRepo = new TenantRepository(ctx);
        var sellerRepo = BuildSellerRepo(seller);

        var stripeApi = Substitute.For<IStripeApiClient>();
        stripeApi.ListBalanceTransactionsAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeBalanceTransactionListResponse(Data: new List<StripeBalanceTransaction>()));

        var sut = new StripeAdvanceReconciler(ctx, tenantRepo, sellerRepo, stripeApi,
            BuildConfig(useStripe: true), NullLogger<StripeAdvanceReconciler>.Instance);

        await sut.ProcessAsync();

        var refreshed = await ctx.TenantConfigs.FirstAsync(c => c.TenantId == tenant.Id);
        refreshed.LastStripeAdvanceReconcileAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_RecoversInstallments_FromBalanceTransactions()
    {
        using var ctx = CreateContext();
        var (tenant, config, seller) = SeedTenant(ctx);

        // Captura mock: reserve já debitado e exposure subiu
        config.TopUpAdvanceReserve(100_000); // R$ 1.000 inicial
        config.DebitAdvanceReserve(54_136);  // R$ 541.36 consumidos
        seller.IncreaseAdvanceExposure(541.36m);
        await ctx.SaveChangesAsync();

        var chargeId = $"ch_{Guid.NewGuid():N}";
        var tx = SeedAdvanceTx(ctx, tenant.Id, seller.Id, chargeId,
            netAmount: 561m, installments: 6, advanceFee: 19.64m);

        // Balance txn da Stripe: R$ 280,50 chegou (= 50% do net, ou 3 parcelas das 6)
        var bt = new StripeBalanceTransaction(
            Id: "bt_1", Type: "charge", Amount: 28050, Net: 28050, Currency: "brl",
            Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Source: chargeId, Status: "available");

        var stripeApi = Substitute.For<IStripeApiClient>();
        stripeApi.ListBalanceTransactionsAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeBalanceTransactionListResponse(Data: new List<StripeBalanceTransaction> { bt }));

        var tenantRepo = new TenantRepository(ctx);
        var sellerRepo = BuildSellerRepo(seller);
        var sut = new StripeAdvanceReconciler(ctx, tenantRepo, sellerRepo, stripeApi,
            BuildConfig(useStripe: true), NullLogger<StripeAdvanceReconciler>.Instance);

        await sut.ProcessAsync();

        // fraction = 28050 / 56100 = 0.5; floor(0.5 × 6) = 3 parcelas recuperadas
        var refreshedTx = await ctx.Transactions.FirstAsync(t => t.Id == tx.Id);
        refreshedTx.AdvanceRecoveredInstallmentCount.Should().Be(3);

        // perInstallmentNet = (561 - 19.64) / 6 = 90.227
        // amountToRecover = 90.227 × 3 = 270.68 (round)
        var refreshedConfig = await ctx.TenantConfigs.FirstAsync(c => c.Id == config.Id);
        refreshedConfig.PlatformAdvanceReserveCents.Should().BeGreaterThan(46_424,
            "reserve foi creditada de volta com 3/6 parcelas recuperadas");

        // Seller mutado in-place — verificamos o ref direto (já que mocking)
        seller.AdvanceExposureCurrent.Should().BeLessThan(541.36m,
            "exposure foi decrementado conforme parcelas recuperadas");
    }

    [Fact]
    public async Task ProcessAsync_IgnoresPendingBalanceTransactions()
    {
        // Status="pending" = ainda não disponível pro platform balance — não credita
        using var ctx = CreateContext();
        var (tenant, _, seller) = SeedTenant(ctx);
        var chargeId = "ch_pending";
        SeedAdvanceTx(ctx, tenant.Id, seller.Id, chargeId);

        var bt = new StripeBalanceTransaction(
            Id: "bt_pending", Type: "charge", Amount: 56100, Net: 56100, Currency: "brl",
            Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Source: chargeId, Status: "pending"); // ← não-available

        var stripeApi = Substitute.For<IStripeApiClient>();
        stripeApi.ListBalanceTransactionsAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(new StripeBalanceTransactionListResponse(Data: new List<StripeBalanceTransaction> { bt }));

        var tenantRepo = new TenantRepository(ctx);
        var sellerRepo = BuildSellerRepo(seller);
        var sut = new StripeAdvanceReconciler(ctx, tenantRepo, sellerRepo, stripeApi,
            BuildConfig(useStripe: true), NullLogger<StripeAdvanceReconciler>.Instance);

        await sut.ProcessAsync();

        var tx = await ctx.Transactions.FirstAsync();
        tx.AdvanceRecoveredInstallmentCount.Should().Be(0, "pending balance_txn não credita");
    }

    [Fact]
    public async Task ProcessAsync_Idempotent_SecondRunNoOp()
    {
        // Re-run no mesmo dia processa só balance_txns novas via cursor advance.
        using var ctx = CreateContext();
        var (tenant, config, seller) = SeedTenant(ctx);
        config.TopUpAdvanceReserve(100_000);
        config.DebitAdvanceReserve(54_136);
        seller.IncreaseAdvanceExposure(541.36m);
        await ctx.SaveChangesAsync();

        var chargeId = "ch_idem";
        var tx = SeedAdvanceTx(ctx, tenant.Id, seller.Id, chargeId,
            netAmount: 561m, installments: 6, advanceFee: 19.64m);

        var bt = new StripeBalanceTransaction(
            Id: "bt_idem", Type: "charge", Amount: 56100, Net: 56100, Currency: "brl",
            Created: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Source: chargeId, Status: "available");

        var stripeApi = Substitute.For<IStripeApiClient>();
        int callCount = 0;
        stripeApi.ListBalanceTransactionsAsync(Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<long?>(),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(_ =>
            {
                callCount++;
                // 1ª chamada: retorna o evento; 2ª chamada (cursor avançou): retorna vazio
                return callCount == 1
                    ? new StripeBalanceTransactionListResponse(Data: new List<StripeBalanceTransaction> { bt })
                    : new StripeBalanceTransactionListResponse(Data: new List<StripeBalanceTransaction>());
            });

        var tenantRepo = new TenantRepository(ctx);
        var sellerRepo = BuildSellerRepo(seller);
        var sut = new StripeAdvanceReconciler(ctx, tenantRepo, sellerRepo, stripeApi,
            BuildConfig(useStripe: true), NullLogger<StripeAdvanceReconciler>.Instance);

        await sut.ProcessAsync(); // 1ª — recupera tudo
        var afterFirst = (await ctx.Transactions.FirstAsync(t => t.Id == tx.Id)).AdvanceRecoveredInstallmentCount;
        afterFirst.Should().Be(6);

        await sut.ProcessAsync(); // 2ª — nada novo

        var afterSecond = (await ctx.Transactions.FirstAsync(t => t.Id == tx.Id)).AdvanceRecoveredInstallmentCount;
        afterSecond.Should().Be(6, "contador monotônico, não passa de Installments");
    }
}
