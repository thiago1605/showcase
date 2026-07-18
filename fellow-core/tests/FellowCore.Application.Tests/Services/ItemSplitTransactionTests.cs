using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Common;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Rails;
using FellowCore.Application.Modules.Transactions.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

/// <summary>
/// Tests that item-level split resolution uses percentage-based allocation
/// so that splits never exceed the net amount after fees.
/// </summary>
public class ItemSplitTransactionTests
{
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly ITransactionItemRepository _transactionItemRepository = Substitute.For<ITransactionItemRepository>();
    private readonly IPaymentIntentRepository _paymentIntentRepository = Substitute.For<IPaymentIntentRepository>();
    private readonly IRefundIntentRepository _refundIntentRepository = Substitute.For<IRefundIntentRepository>();
    private readonly ISplitRuleRepository _splitRuleRepository = Substitute.For<ISplitRuleRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPricingService _pricingService = Substitute.For<IPricingService>();
    private readonly IProviderCostService _providerCostService = Substitute.For<IProviderCostService>();
    private readonly TransactionService _sut;

    private Transaction? _capturedTransaction;
    private readonly Guid _tenantId;
    private readonly Seller _seller;

    private static readonly PayerDto DefaultPayer = new("João Silva", "52998224725", "joao@email.com");

    public ItemSplitTransactionTests()
    {
        var providerFactory = Substitute.For<IPaymentProviderFactory>();
        var paymentProvider = Substitute.For<IPaymentProvider>();
        paymentProvider.ProcessPaymentAsync(
                Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<CreateTransactionDto>(), Arg.Any<decimal>(), Arg.Any<string?>())
            .Returns(new GatewayPaymentDetails("prov_tx_001"));

        providerFactory.GetProvider(Arg.Any<PaymentProvider>()).Returns(paymentProvider);

        var openPixApi = Substitute.For<IOpenPixApiClient>();
        var config = Substitute.For<IConfiguration>();
        var ledgerService = Substitute.For<ILedgerService>();

        _pricingService.CalculatePlatformFeeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<PaymentType>(), Arg.Any<int>(), Arg.Any<decimal>())
            .Returns(new PlatformFeeResult(2m, 98m));
        _providerCostService.CalculateProviderCostAsync(Arg.Any<PaymentProvider>(), Arg.Any<PaymentType>(), Arg.Any<decimal>())
            .Returns(0.80m);

        var railRouter = new RailRouter([new OpenPixRail(providerFactory), new StripeCardRail(providerFactory)]);

        _transactionRepository.When(r => r.Add(Arg.Any<Transaction>()))
            .Do(ci => _capturedTransaction = ci.Arg<Transaction>());

        // Create a seller with 2% PIX fee (feePixIn = 2.0)
        // For R$100 PIX: fee = R$2, net = R$98
        var tenant = BuildTenantWithConfig();
        _tenantId = tenant.Id;
        _seller = Seller.Create(
            tenantId: _tenantId,
            legalName: "Primary Seller",
            document: "12345678000100",
            email: "seller@test.com",
            webhookSecret: "secret",
            feePixIn: 2.0m,
            feeDebit: 2.0m,
            feeCreditCash: 2.0m,
            feeCreditInstallment: 2.0m);

        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(tenant);
        _sellerRepository.GetByIdAsync(Arg.Any<Guid>(), _seller.Id).Returns(_seller);

        _sut = new TransactionService(
            _tenantRepository, _sellerRepository, _transactionRepository,
            _transactionItemRepository, _paymentIntentRepository,
            _refundIntentRepository, Substitute.For<ITransactionInstallmentRepository>(),
            _splitRuleRepository,
            _unitOfWork, railRouter, TimeProvider.System, openPixApi, ledgerService,
            _pricingService, _providerCostService, Substitute.For<IAppMetrics>(), config,
            Substitute.For<ILogger<TransactionService>>());
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_ShouldNotThrow_WhenItemsSumToGross()
    {
        // R$100 sale, fee R$2 (PIX 2%), net R$98. Items sum to R$100 (gross).
        // Without the percentage fix, this would throw "splits exceed net amount".
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();

        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Produto A", Quantity: 1, UnitAmount: 60m, SellerId: sellerA),
                new("Produto B", Quantity: 1, UnitAmount: 40m, SellerId: sellerB)
            });

        var act = () => _sut.CreateAsync(_tenantId, request);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_SplitsSumToNetAmount()
    {
        // R$100 sale, fee R$2, net R$98. Two sellers: 60% and 40%.
        // Splits should distribute R$98 proportionally, not R$100.
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();

        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Produto A", Quantity: 1, UnitAmount: 60m, SellerId: sellerA),
                new("Produto B", Quantity: 1, UnitAmount: 40m, SellerId: sellerB)
            });

        await _sut.CreateAsync(_tenantId, request);

        _capturedTransaction.Should().NotBeNull();
        var splits = _capturedTransaction!.Splits;
        splits.Should().HaveCount(2);

        var totalSplitAmount = splits.Sum(s => s.Amount);
        // Must be <= net (R$98), not gross (R$100)
        totalSplitAmount.Should().BeLessThanOrEqualTo(98m);
        totalSplitAmount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_MultipleSellers_ProportionalDistribution()
    {
        // 3 sellers: R$50, R$30, R$20 from items. Net = R$98.
        // Percentages: 50%, 30%, 20%
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();
        var sellerC = Guid.NewGuid();

        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Produto A", Quantity: 1, UnitAmount: 50m, SellerId: sellerA),
                new("Produto B", Quantity: 1, UnitAmount: 30m, SellerId: sellerB),
                new("Produto C", Quantity: 1, UnitAmount: 20m, SellerId: sellerC)
            });

        await _sut.CreateAsync(_tenantId, request);

        _capturedTransaction.Should().NotBeNull();
        var splits = _capturedTransaction!.Splits;
        splits.Should().HaveCount(3);

        // netAmount = 98. Each split = net * (itemGross / cartGross)
        var splitA = splits.First(s => s.RecipientId == sellerA.ToString());
        var splitB = splits.First(s => s.RecipientId == sellerB.ToString());
        var splitC = splits.First(s => s.RecipientId == sellerC.ToString());

        splitA.Amount.Should().Be(RoundingPolicy.Round(98m * 50m / 100m)); // 49.00
        splitB.Amount.Should().Be(RoundingPolicy.Round(98m * 30m / 100m)); // 29.40
        splitC.Amount.Should().Be(RoundingPolicy.Round(98m * 20m / 100m)); // 19.60
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_SameSellerMultipleItems_AggregatesCorrectly()
    {
        // Same seller owns multiple items: should aggregate into single split percentage
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();

        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Produto A1", Quantity: 2, UnitAmount: 25m, SellerId: sellerA), // 50
                new("Produto A2", Quantity: 1, UnitAmount: 10m, SellerId: sellerA), // 10 → total A = 60
                new("Produto B1", Quantity: 1, UnitAmount: 40m, SellerId: sellerB)  // 40
            });

        await _sut.CreateAsync(_tenantId, request);

        _capturedTransaction.Should().NotBeNull();
        var splits = _capturedTransaction!.Splits;
        splits.Should().HaveCount(2); // aggregated per seller

        var splitA = splits.First(s => s.RecipientId == sellerA.ToString());
        var splitB = splits.First(s => s.RecipientId == sellerB.ToString());

        // Seller A: 60/100 = 60% of net 98 = 58.80
        splitA.Amount.Should().Be(RoundingPolicy.Round(98m * 60m / 100m)); // 58.80
        splitB.Amount.Should().Be(RoundingPolicy.Round(98m * 40m / 100m)); // 39.20
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_HighFee_StillSucceeds()
    {
        // High fee scenario: feePixIn = 10% → R$100 sale, fee R$10, net R$90.
        var highFeeSeller = Seller.Create(
            tenantId: _tenantId,
            legalName: "High Fee Seller",
            document: "98765432000199",
            email: "hfee@test.com",
            webhookSecret: "secret2",
            feePixIn: 10.0m);

        _sellerRepository.GetByIdAsync(Arg.Any<Guid>(), highFeeSeller.Id).Returns(highFeeSeller);

        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();

        var request = new CreateTransactionDto(
            SellerId: highFeeSeller.Id, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Produto A", Quantity: 1, UnitAmount: 70m, SellerId: sellerA),
                new("Produto B", Quantity: 1, UnitAmount: 30m, SellerId: sellerB)
            });

        var act = () => _sut.CreateAsync(_tenantId, request);

        await act.Should().NotThrowAsync();

        _capturedTransaction.Should().NotBeNull();
        var totalSplitAmount = _capturedTransaction!.Splits.Sum(s => s.Amount);
        // Net = 90, splits must not exceed it
        totalSplitAmount.Should().BeLessThanOrEqualTo(90m);
        // 70% of 90 = 63 + 30% of 90 = 27 = 90
        totalSplitAmount.Should().Be(RoundingPolicy.Round(90m * 70m / 100m) + RoundingPolicy.Round(90m * 30m / 100m));
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_WithSplitRule_ResolvesProportionally()
    {
        // Item has a SplitRuleId — recipients from rule get proportional share of net
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();

        var rule = SplitRule.Create(_tenantId, "Test Rule").Value;
        rule.AddRecipient(sellerA, percentage: 70m, fixedAmount: null, priority: 0);
        rule.AddRecipient(sellerB, percentage: 30m, fixedAmount: null, priority: 1);

        _splitRuleRepository.GetByIdWithRecipientsAsync(_tenantId, rule.Id).Returns(rule);

        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Serviço", Quantity: 1, UnitAmount: 100m, SplitRuleId: rule.Id)
            });

        await _sut.CreateAsync(_tenantId, request);

        _capturedTransaction.Should().NotBeNull();
        var splits = _capturedTransaction!.Splits;
        splits.Should().HaveCount(2);

        // Rule 70/30 on R$100 item → gross 70 and 30, cart total = 100
        // Percentages: 70% and 30% of net R$98
        var splitA = splits.First(s => s.RecipientId == sellerA.ToString());
        var splitB = splits.First(s => s.RecipientId == sellerB.ToString());

        splitA.Amount.Should().Be(RoundingPolicy.Round(98m * 70m / 100m)); // 68.60
        splitB.Amount.Should().Be(RoundingPolicy.Round(98m * 30m / 100m)); // 29.40
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_RoundingResidual_DoesNotExceedNet()
    {
        // Items with amounts causing rounding: 3 items of R$33.33 each (total 99.99)
        // With feePixIn = 2%: fee = 99.99 * 2/100 = 2.00, net = 97.99
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();
        var sellerC = Guid.NewGuid();

        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 99.99m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Produto A", Quantity: 1, UnitAmount: 33.33m, SellerId: sellerA),
                new("Produto B", Quantity: 1, UnitAmount: 33.33m, SellerId: sellerB),
                new("Produto C", Quantity: 1, UnitAmount: 33.33m, SellerId: sellerC)
            });

        await _sut.CreateAsync(_tenantId, request);

        _capturedTransaction.Should().NotBeNull();
        // Net = 99.99 - round(99.99 * 2 / 100) = 99.99 - 2.00 = 97.99
        var netAmount = 99.99m - Math.Round(99.99m * 2m / 100m, 2);
        var totalSplitAmount = _capturedTransaction!.Splits.Sum(s => s.Amount);
        totalSplitAmount.Should().BeLessThanOrEqualTo(netAmount);
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_PartialCart_OnlySplitsAssignedItems()
    {
        // Cart R$100. Item A: R$40 with recipient seller. Item B: R$60 no seller/rule.
        // Recipient should get 40% of net (not 100%). Primary keeps residual.
        var recipientSeller = Guid.NewGuid();

        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Produto A", Quantity: 1, UnitAmount: 40m, SellerId: recipientSeller),
                new("Produto B", Quantity: 1, UnitAmount: 60m) // no seller, stays with primary
            });

        await _sut.CreateAsync(_tenantId, request);

        _capturedTransaction.Should().NotBeNull();
        var splits = _capturedTransaction!.Splits;
        // Only the assigned recipient gets a split; primary seller keeps residual implicitly
        splits.Should().HaveCount(1);

        var split = splits.Single();
        split.RecipientId.Should().Be(recipientSeller.ToString());
        // 40/100 = 40% of net R$98 = 39.20
        split.Amount.Should().Be(RoundingPolicy.Round(98m * 40m / 100m)); // 39.20
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_FixedAmountRule_UsesCorrectPercentage()
    {
        // Item R$100 with SplitRule containing fixed R$10 for seller A.
        // Seller A should get 10% of net (R$10/R$100 gross = 10%), not 100%.
        var sellerA = Guid.NewGuid();

        var rule = SplitRule.Create(_tenantId, "Fixed Rule").Value;
        rule.AddRecipient(sellerA, percentage: null, fixedAmount: 10m, priority: 0);

        _splitRuleRepository.GetByIdWithRecipientsAsync(_tenantId, rule.Id).Returns(rule);

        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Serviço", Quantity: 1, UnitAmount: 100m, SplitRuleId: rule.Id)
            });

        await _sut.CreateAsync(_tenantId, request);

        _capturedTransaction.Should().NotBeNull();
        var splits = _capturedTransaction!.Splits;
        splits.Should().HaveCount(1);

        var split = splits.Single();
        split.RecipientId.Should().Be(sellerA.ToString());
        // Fixed R$10 / cart total R$100 = 10% → 10% of net R$98 = R$9.80
        split.Amount.Should().Be(RoundingPolicy.Round(98m * 10m / 100m)); // 9.80
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_MixedAssignment_CorrectProportions()
    {
        // Cart R$200: Item A R$80 (sellerA), Item B R$50 (sellerB), Item C R$70 (no seller)
        // feePixIn=2% on R$200 → fee=R$4, net=R$196
        // sellerA: 80/200=40%, sellerB: 50/200=25%, primary residual: 70/200=35%
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();

        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 200m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho misto", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Produto A", Quantity: 1, UnitAmount: 80m, SellerId: sellerA),
                new("Produto B", Quantity: 1, UnitAmount: 50m, SellerId: sellerB),
                new("Produto C", Quantity: 1, UnitAmount: 70m) // primary seller residual
            });

        await _sut.CreateAsync(_tenantId, request);

        _capturedTransaction.Should().NotBeNull();
        var splits = _capturedTransaction!.Splits;
        splits.Should().HaveCount(2); // only assigned sellers

        // net = 200 - round(200*2/100) = 200 - 4 = 196
        decimal net = 196m;
        var splitA = splits.First(s => s.RecipientId == sellerA.ToString());
        var splitB = splits.First(s => s.RecipientId == sellerB.ToString());

        splitA.Amount.Should().Be(RoundingPolicy.Round(net * 80m / 200m)); // 78.40
        splitB.Amount.Should().Be(RoundingPolicy.Round(net * 50m / 200m)); // 49.00

        // Total splits < net (residual stays with primary)
        var totalSplits = splits.Sum(s => s.Amount);
        totalSplits.Should().BeLessThan(net);
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_AllItemsWithoutSeller_NoSplitsCreated()
    {
        // If no items have a seller or rule, no splits should be created
        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Produto A", Quantity: 1, UnitAmount: 60m),
                new("Produto B", Quantity: 1, UnitAmount: 40m)
            });

        await _sut.CreateAsync(_tenantId, request);

        _capturedTransaction.Should().NotBeNull();
        _capturedTransaction!.Splits.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ItemSplit_ShouldThrow_WhenItemsTotalDiffersFromAmount()
    {
        // Items sum to R$80 but Amount is R$100 — mismatch should throw
        var sellerA = Guid.NewGuid();

        var request = new CreateTransactionDto(
            SellerId: _seller.Id, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Carrinho", Payer: DefaultPayer,
            Items: new List<TransactionItemDto>
            {
                new("Produto A", Quantity: 1, UnitAmount: 50m, SellerId: sellerA),
                new("Produto B", Quantity: 1, UnitAmount: 30m)
            });

        var act = () => _sut.CreateAsync(_tenantId, request);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*soma dos itens*difere*");
    }

    private static Tenant BuildTenantWithConfig()
    {
        var tenant = Tenant.Create("Test Tenant", "test-tenant", "testhash", "pk_test_xxxx", "hash");
        tenant.CreateDefaultConfig();
        tenant.Config!.SetStripeChargeMode(StripeChargeMode.DESTINATION_CHARGE);
        return tenant;
    }
}
