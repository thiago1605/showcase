using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Modules.Splits.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Tests.Services;

public class ItemSplitResolverTests
{
    private readonly ITransactionItemRepository _itemRepo = Substitute.For<ITransactionItemRepository>();
    private readonly ISplitRuleRepository _splitRuleRepo = Substitute.For<ISplitRuleRepository>();
    private readonly ISplitAllocationRepository _allocationRepo = Substitute.For<ISplitAllocationRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ItemSplitResolver _sut;

    public ItemSplitResolverTests()
    {
        _sut = new ItemSplitResolver(_itemRepo, _splitRuleRepo, _allocationRepo, _unitOfWork);
    }

    [Fact]
    public async Task ResolveFromItemsAsync_NoItems_ReturnsEmpty()
    {
        var tenantId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        _itemRepo.GetByTransactionIdAsync(tenantId, transactionId).Returns(new List<TransactionItem>());

        var result = await _sut.ResolveFromItemsAsync(tenantId, transactionId);

        result.HasItemSplits.Should().BeFalse();
        result.Recipients.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveFromItemsAsync_ItemsWithDirectSeller_AggregatesBySeller()
    {
        var tenantId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();

        var items = new List<TransactionItem>
        {
            TransactionItem.Create(tenantId, transactionId, "Item A", 2, 50m, sellerId: sellerA),
            TransactionItem.Create(tenantId, transactionId, "Item B", 1, 30m, sellerId: sellerB),
            TransactionItem.Create(tenantId, transactionId, "Item C", 1, 20m, sellerId: sellerA),
        };

        _itemRepo.GetByTransactionIdAsync(tenantId, transactionId).Returns(items);

        var result = await _sut.ResolveFromItemsAsync(tenantId, transactionId);

        result.HasItemSplits.Should().BeTrue();
        result.Recipients.Should().HaveCount(2);
        result.Recipients.First(r => r.SellerId == sellerA).Amount.Should().Be(120m); // 2*50 + 1*20
        result.Recipients.First(r => r.SellerId == sellerB).Amount.Should().Be(30m);
        await _allocationRepo.Received(1).AddRangeAsync(Arg.Is<IEnumerable<SplitAllocation>>(a => a.Count() == 3));
    }

    [Fact]
    public async Task ResolveFromItemsAsync_ItemsWithSplitRule_ResolvesFromRule()
    {
        var tenantId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var recipientSeller = Guid.NewGuid();

        var items = new List<TransactionItem>
        {
            TransactionItem.Create(tenantId, transactionId, "Product X", 1, 200m, splitRuleId: ruleId),
        };

        var rule = BuildSplitRuleWithRecipient(tenantId, ruleId, recipientSeller, percentage: 50m);

        _itemRepo.GetByTransactionIdAsync(tenantId, transactionId).Returns(items);
        _splitRuleRepo.GetByIdWithRecipientsAsync(tenantId, ruleId).Returns(rule);

        var result = await _sut.ResolveFromItemsAsync(tenantId, transactionId);

        result.HasItemSplits.Should().BeTrue();
        result.Recipients.Should().HaveCount(1);
        result.Recipients[0].SellerId.Should().Be(recipientSeller);
        result.Recipients[0].Amount.Should().Be(100m); // 50% of 200
    }

    [Fact]
    public async Task ResolveFromItemsAsync_ItemsWithNoSellerOrRule_ReturnsEmpty()
    {
        var tenantId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        var items = new List<TransactionItem>
        {
            TransactionItem.Create(tenantId, transactionId, "Generic Item", 1, 100m),
        };

        _itemRepo.GetByTransactionIdAsync(tenantId, transactionId).Returns(items);

        var result = await _sut.ResolveFromItemsAsync(tenantId, transactionId);

        result.HasItemSplits.Should().BeFalse();
        result.Recipients.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveFromItemsAsync_SameRecipientAcrossItems_AggregatesAmount()
    {
        var tenantId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var ruleId1 = Guid.NewGuid();
        var ruleId2 = Guid.NewGuid();
        var sharedRecipient = Guid.NewGuid();

        var items = new List<TransactionItem>
        {
            TransactionItem.Create(tenantId, transactionId, "Item 1", 1, 100m, splitRuleId: ruleId1),
            TransactionItem.Create(tenantId, transactionId, "Item 2", 1, 200m, splitRuleId: ruleId2),
        };

        var rule1 = BuildSplitRuleWithRecipient(tenantId, ruleId1, sharedRecipient, percentage: 30m);
        var rule2 = BuildSplitRuleWithRecipient(tenantId, ruleId2, sharedRecipient, percentage: 20m);

        _itemRepo.GetByTransactionIdAsync(tenantId, transactionId).Returns(items);
        _splitRuleRepo.GetByIdWithRecipientsAsync(tenantId, ruleId1).Returns(rule1);
        _splitRuleRepo.GetByIdWithRecipientsAsync(tenantId, ruleId2).Returns(rule2);

        var result = await _sut.ResolveFromItemsAsync(tenantId, transactionId);

        result.HasItemSplits.Should().BeTrue();
        result.Recipients.Should().HaveCount(1);
        result.Recipients[0].Amount.Should().Be(70m); // 30% of 100 + 20% of 200
    }

    private static SplitRule BuildSplitRuleWithRecipient(Guid tenantId, Guid ruleId, Guid recipientSellerId, decimal? percentage = null, decimal? fixedAmount = null)
    {
        var rule = SplitRule.Create(tenantId, $"Rule-{ruleId}").Value;
        typeof(SplitRule).GetProperty("Id")!.SetValue(rule, ruleId);
        rule.AddRecipient(recipientSellerId, percentage, fixedAmount, 0);
        return rule;
    }
}
