using FluentAssertions;
using NSubstitute;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using FellowCore.Infrastructure.Repositories;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FellowCore.Application.Tests.Processors;

/// <summary>
/// Cobre o refresh diário de <see cref="SellerRiskProfile"/>. Usa AppDbContext
/// in-memory + repo real (não mock) pra exercitar JOINs e queries de fato.
/// </summary>
public class SellerRiskProfileRefreshProcessorTests
{
    private static AppDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"RiskRefresh_{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(opts);
    }

    private static Seller AddSeller(AppDbContext ctx, Guid tenantId, int ageDays = 200)
    {
        var seller = Seller.Create(
            tenantId: tenantId, legalName: "X", document: $"{Guid.NewGuid():N}".Substring(0, 11),
            email: $"x{Guid.NewGuid():N}@x.com", webhookSecret: "ws",
            preferredProvider: PaymentProvider.STRIPE,
            externalAccountId: "acct_x",
            encryptedAccessToken: "enc");
        typeof(Seller).GetProperty("CreatedAt")!.SetValue(seller, DateTime.UtcNow.AddDays(-ageDays));
        ctx.Sellers.Add(seller);
        return seller;
    }

    private static Transaction AddTransaction(AppDbContext ctx, Guid tenantId, Guid sellerId,
        TransactionStatus status = TransactionStatus.CAPTURED,
        decimal netAmount = 100m,
        DateTime? createdAt = null)
    {
        var tx = Transaction.Create(
            tenantId: tenantId, amount: netAmount + 5m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 1, feeAmount: 5m, netAmount: netAmount,
            expectedSettlementDate: null, providerTxId: $"pi_{Guid.NewGuid():N}",
            sellerId: sellerId).Value;
        if (createdAt.HasValue)
            typeof(Transaction).GetProperty("CreatedAt")!.SetValue(tx, createdAt.Value);
        if (status != TransactionStatus.PROCESSING)
        {
            // bypass state machine — simula múltiplas transições
            typeof(Transaction).GetProperty("Status")!.SetValue(tx, status);
        }
        ctx.Transactions.Add(tx);
        return tx;
    }

    private static Dispute AddLostDispute(AppDbContext ctx, Guid transactionId, DateTime? createdAt = null)
    {
        var dispute = Dispute.Create(
            tenantId: Guid.NewGuid(), transactionId: transactionId, sellerId: null,
            externalDisputeId: $"dp_{Guid.NewGuid():N}", amount: 50m, reason: "fraudulent",
            now: createdAt);
        dispute.Lose();
        // Lose() seta UpdatedAt — restaura CreatedAt original se necessário
        if (createdAt.HasValue)
            typeof(Dispute).GetProperty("CreatedAt")!.SetValue(dispute, createdAt.Value);
        ctx.Disputes.Add(dispute);
        return dispute;
    }

    [Fact]
    public async Task ProcessAsync_NoActiveSellers_NoOp()
    {
        using var ctx = CreateContext();
        var repo = new SellerRiskProfileRepository(ctx);
        var processor = new SellerRiskProfileRefreshProcessor(ctx, repo,
            NullLogger<SellerRiskProfileRefreshProcessor>.Instance);

        await processor.ProcessAsync();

        var profiles = await ctx.Set<SellerRiskProfile>().CountAsync();
        profiles.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_CreatesProfile_ForActiveSellerWithTransactions()
    {
        using var ctx = CreateContext();
        var tenantId = Guid.NewGuid();
        var seller = AddSeller(ctx, tenantId);
        // 15 captured TXs nos últimos 60 dias
        for (int i = 0; i < 15; i++)
            AddTransaction(ctx, tenantId, seller.Id, TransactionStatus.CAPTURED,
                netAmount: 100m, createdAt: DateTime.UtcNow.AddDays(-i * 4));
        await ctx.SaveChangesAsync();

        var repo = new SellerRiskProfileRepository(ctx);
        var processor = new SellerRiskProfileRefreshProcessor(ctx, repo,
            NullLogger<SellerRiskProfileRefreshProcessor>.Instance);

        await processor.ProcessAsync();

        var profile = await ctx.Set<SellerRiskProfile>().FirstOrDefaultAsync(p => p.SellerId == seller.Id);
        profile.Should().NotBeNull();
        profile!.CapturedCount90d.Should().Be(15);
        profile.ChargebackLostCount90d.Should().Be(0);
        profile.ChargebackRate.Should().Be(0m);
        profile.CapturedVolume90d.Should().Be(1500m);
    }

    [Fact]
    public async Task ProcessAsync_ComputesChargebackRate_WhenSampleAboveThreshold()
    {
        using var ctx = CreateContext();
        var tenantId = Guid.NewGuid();
        var seller = AddSeller(ctx, tenantId);
        // 20 capturadas + 2 disputes lost = 10% rate (acima do 1% padrão)
        var txs = new List<Transaction>();
        for (int i = 0; i < 20; i++)
            txs.Add(AddTransaction(ctx, tenantId, seller.Id, TransactionStatus.CAPTURED, netAmount: 100m,
                createdAt: DateTime.UtcNow.AddDays(-i)));
        await ctx.SaveChangesAsync();

        // Dispute relacionada às 2 primeiras TXs
        AddLostDispute(ctx, txs[0].Id, DateTime.UtcNow.AddDays(-10));
        AddLostDispute(ctx, txs[1].Id, DateTime.UtcNow.AddDays(-5));
        await ctx.SaveChangesAsync();

        var repo = new SellerRiskProfileRepository(ctx);
        var processor = new SellerRiskProfileRefreshProcessor(ctx, repo,
            NullLogger<SellerRiskProfileRefreshProcessor>.Instance);

        await processor.ProcessAsync();

        var profile = await ctx.Set<SellerRiskProfile>().FirstOrDefaultAsync(p => p.SellerId == seller.Id);
        profile.Should().NotBeNull();
        profile!.CapturedCount90d.Should().Be(20);
        profile.ChargebackLostCount90d.Should().Be(2);
        profile.ChargebackRate.Should().Be(0.1m, "2 / 20 = 10%");
    }

    [Fact]
    public async Task ProcessAsync_IgnoresChargebackRate_WhenSampleBelowThreshold()
    {
        // SellerRiskProfile.CreateOrUpdate dá rate = 0 se < 10 TXs (sample muito pequeno).
        using var ctx = CreateContext();
        var tenantId = Guid.NewGuid();
        var seller = AddSeller(ctx, tenantId);
        // Só 5 TXs e 2 chargebacks = 40% MAS sample < 10
        var txs = new List<Transaction>();
        for (int i = 0; i < 5; i++)
            txs.Add(AddTransaction(ctx, tenantId, seller.Id, TransactionStatus.CAPTURED, netAmount: 100m,
                createdAt: DateTime.UtcNow.AddDays(-i)));
        await ctx.SaveChangesAsync();
        AddLostDispute(ctx, txs[0].Id, DateTime.UtcNow.AddDays(-2));
        AddLostDispute(ctx, txs[1].Id, DateTime.UtcNow.AddDays(-3));
        await ctx.SaveChangesAsync();

        var repo = new SellerRiskProfileRepository(ctx);
        var processor = new SellerRiskProfileRefreshProcessor(ctx, repo,
            NullLogger<SellerRiskProfileRefreshProcessor>.Instance);

        await processor.ProcessAsync();

        var profile = await ctx.Set<SellerRiskProfile>().FirstOrDefaultAsync(p => p.SellerId == seller.Id);
        profile!.ChargebackLostCount90d.Should().Be(2, "dados crus preservados");
        profile.ChargebackRate.Should().Be(0m, "rate zerada por amostra estatisticamente insignificante");
    }

    [Fact]
    public async Task ProcessAsync_UpdatesExistingProfile_OnSecondRun()
    {
        using var ctx = CreateContext();
        var tenantId = Guid.NewGuid();
        var seller = AddSeller(ctx, tenantId);
        for (int i = 0; i < 10; i++)
            AddTransaction(ctx, tenantId, seller.Id, TransactionStatus.CAPTURED, netAmount: 50m,
                createdAt: DateTime.UtcNow.AddDays(-i));
        await ctx.SaveChangesAsync();

        var repo = new SellerRiskProfileRepository(ctx);
        var processor = new SellerRiskProfileRefreshProcessor(ctx, repo,
            NullLogger<SellerRiskProfileRefreshProcessor>.Instance);

        await processor.ProcessAsync();
        var profile1 = await ctx.Set<SellerRiskProfile>().FirstAsync(p => p.SellerId == seller.Id);
        profile1.CapturedCount90d.Should().Be(10);
        var firstId = profile1.Id;

        // Adiciona mais 5 TXs e re-roda
        for (int i = 0; i < 5; i++)
            AddTransaction(ctx, tenantId, seller.Id, TransactionStatus.CAPTURED, netAmount: 100m,
                createdAt: DateTime.UtcNow.AddDays(-i));
        await ctx.SaveChangesAsync();

        await processor.ProcessAsync();
        var profile2 = await ctx.Set<SellerRiskProfile>().FirstAsync(p => p.SellerId == seller.Id);
        profile2.Id.Should().Be(firstId, "mesmo profile atualizado, não criado novo");
        profile2.CapturedCount90d.Should().Be(15);
        profile2.CapturedVolume90d.Should().Be(1000m);
        // unique índice em SellerId — apenas 1 profile por seller
        var totalProfiles = await ctx.Set<SellerRiskProfile>().CountAsync(p => p.SellerId == seller.Id);
        totalProfiles.Should().Be(1);
    }

    [Fact]
    public async Task ProcessAsync_IgnoresInactiveSellers()
    {
        using var ctx = CreateContext();
        var tenantId = Guid.NewGuid();
        var activeSeller = AddSeller(ctx, tenantId);
        var suspendedSeller = AddSeller(ctx, tenantId);
        suspendedSeller.Suspend();
        await ctx.SaveChangesAsync();

        var repo = new SellerRiskProfileRepository(ctx);
        var processor = new SellerRiskProfileRefreshProcessor(ctx, repo,
            NullLogger<SellerRiskProfileRefreshProcessor>.Instance);

        await processor.ProcessAsync();

        var profiles = await ctx.Set<SellerRiskProfile>().ToListAsync();
        profiles.Should().HaveCount(1);
        profiles[0].SellerId.Should().Be(activeSeller.Id);
    }
}
