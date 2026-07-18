using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Splits.DTOs;
using FellowCore.Application.Modules.Splits.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class SplitRuleServiceTests
{
    private readonly ISplitRuleRepository _splitRuleRepository = Substitute.For<ISplitRuleRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ILogger<SplitRuleService> _logger = Substitute.For<ILogger<SplitRuleService>>();
    private readonly SplitRuleService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId1 = Guid.NewGuid();
    private static readonly Guid SellerId2 = Guid.NewGuid();

    public SplitRuleServiceTests()
    {
        _sut = new SplitRuleService(_splitRuleRepository, _sellerRepository, _logger);
    }

    private static Seller CreateSeller(Guid tenantId, Guid sellerId)
    {
        var seller = Seller.Create(
            tenantId: tenantId,
            legalName: "Test Seller",
            document: "12345678901",
            email: "test@example.com",
            webhookSecret: "secret123"
        );
        // Use reflection to set the Id since it's generated internally
        typeof(Seller).GetProperty("Id")!.SetValue(seller, sellerId);
        return seller;
    }

    // --- CreateAsync tests ---

    [Fact]
    public async Task CreateAsync_ShouldSucceed_WithValidData()
    {
        var request = new CreateSplitRuleDto(
            Name: "My Rule",
            Recipients: [
                new SplitRuleRecipientDto(SellerId: SellerId1, Percentage: 60m),
                new SplitRuleRecipientDto(SellerId: SellerId2, Percentage: 40m)
            ]
        );

        _splitRuleRepository.ExistsByNameAsync(TenantId, request.Name).Returns(false);
        _sellerRepository.GetByIdAsync(TenantId, SellerId1).Returns(CreateSeller(TenantId, SellerId1));
        _sellerRepository.GetByIdAsync(TenantId, SellerId2).Returns(CreateSeller(TenantId, SellerId2));

        var result = await _sut.CreateAsync(TenantId, request);

        result.Name.Should().Be("My Rule");
        // Regras nascem como draft (inativas) — o seller precisa ativar explicitamente
        // depois de revisar destinatários/percentuais (POST /split-rules/{id}/activate).
        result.IsActive.Should().BeFalse();
        result.TenantId.Should().Be(TenantId);
        result.Recipients.Should().HaveCount(2);
        _splitRuleRepository.Received(1).Add(Arg.Any<SplitRule>());
        await _splitRuleRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task CreateAsync_ShouldSucceed_WithFixedAmounts()
    {
        var request = new CreateSplitRuleDto(
            Name: "Fixed Rule",
            Recipients: [
                new SplitRuleRecipientDto(SellerId: SellerId1, FixedAmount: 25m, Priority: 1),
                new SplitRuleRecipientDto(SellerId: SellerId2, FixedAmount: 50m, Priority: 2)
            ]
        );

        _splitRuleRepository.ExistsByNameAsync(TenantId, request.Name).Returns(false);
        _sellerRepository.GetByIdAsync(TenantId, SellerId1).Returns(CreateSeller(TenantId, SellerId1));
        _sellerRepository.GetByIdAsync(TenantId, SellerId2).Returns(CreateSeller(TenantId, SellerId2));

        var result = await _sut.CreateAsync(TenantId, request);

        result.Recipients.Should().HaveCount(2);
        result.Recipients.Should().AllSatisfy(r => r.FixedAmount.Should().BeGreaterThan(0));
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowValidation_WhenNoRecipients()
    {
        var request = new CreateSplitRuleDto(
            Name: "Empty Rule",
            Recipients: []
        );

        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Error.Code == "SplitRule.NoRecipients");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowValidation_WhenTooManyRecipients()
    {
        var recipients = Enumerable.Range(0, 51)
            .Select(i => new SplitRuleRecipientDto(SellerId: Guid.NewGuid(), Percentage: 1m))
            .ToList();

        var request = new CreateSplitRuleDto(Name: "Big Rule", Recipients: recipients);

        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Error.Code == "SplitRule.TooManyRecipients");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowConflict_WhenNameAlreadyExists()
    {
        var request = new CreateSplitRuleDto(
            Name: "Duplicate Rule",
            Recipients: [new SplitRuleRecipientDto(SellerId: SellerId1, Percentage: 100m)]
        );

        _splitRuleRepository.ExistsByNameAsync(TenantId, request.Name).Returns(true);

        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<ConflictException>()
            .Where(e => e.Error.Code == "SplitRule.NameAlreadyExists");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowNotFound_WhenSellerNotFound()
    {
        var request = new CreateSplitRuleDto(
            Name: "Rule",
            Recipients: [new SplitRuleRecipientDto(SellerId: SellerId1, Percentage: 100m)]
        );

        _splitRuleRepository.ExistsByNameAsync(TenantId, request.Name).Returns(false);
        _sellerRepository.GetByIdAsync(TenantId, SellerId1).Returns((Seller?)null);

        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<NotFoundException>()
            .Where(e => e.Error.Code == "SplitRule.SellerNotFound");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowValidation_WhenPercentageExceeds100()
    {
        var request = new CreateSplitRuleDto(
            Name: "Bad Rule",
            Recipients: [
                new SplitRuleRecipientDto(SellerId: SellerId1, Percentage: 60m),
                new SplitRuleRecipientDto(SellerId: SellerId2, Percentage: 50m)
            ]
        );

        _splitRuleRepository.ExistsByNameAsync(TenantId, request.Name).Returns(false);
        _sellerRepository.GetByIdAsync(TenantId, SellerId1).Returns(CreateSeller(TenantId, SellerId1));
        _sellerRepository.GetByIdAsync(TenantId, SellerId2).Returns(CreateSeller(TenantId, SellerId2));

        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Error.Code == "SplitRule.PercentageExceeds100");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowValidation_WhenBothPercentageAndFixedAmount()
    {
        var request = new CreateSplitRuleDto(
            Name: "Bad Rule",
            Recipients: [
                new SplitRuleRecipientDto(SellerId: SellerId1, Percentage: 50m, FixedAmount: 25m)
            ]
        );

        _splitRuleRepository.ExistsByNameAsync(TenantId, request.Name).Returns(false);
        _sellerRepository.GetByIdAsync(TenantId, SellerId1).Returns(CreateSeller(TenantId, SellerId1));

        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(e => e.Error.Code == "SplitRule.BothAmountAndPercentage");
    }

    // --- GetByIdAsync tests ---

    [Fact]
    public async Task GetByIdAsync_ShouldSucceed_WhenRuleExists()
    {
        var ruleId = Guid.NewGuid();
        var rule = SplitRule.Create(TenantId, "My Rule").Value;
        rule.AddRecipient(SellerId1, percentage: 100m, fixedAmount: null, priority: 0);

        _splitRuleRepository.GetByIdWithRecipientsAsync(TenantId, ruleId).Returns(rule);

        var result = await _sut.GetByIdAsync(TenantId, ruleId);

        result.Name.Should().Be("My Rule");
        result.Recipients.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldThrowNotFound_WhenRuleDoesNotExist()
    {
        var ruleId = Guid.NewGuid();
        _splitRuleRepository.GetByIdWithRecipientsAsync(TenantId, ruleId).Returns((SplitRule?)null);

        var act = () => _sut.GetByIdAsync(TenantId, ruleId);

        await act.Should().ThrowAsync<NotFoundException>()
            .Where(e => e.Error.Code == "SplitRule.NotFound");
    }

    // --- ListAsync tests ---

    [Fact]
    public async Task ListAsync_ShouldReturnPagedResults()
    {
        var rule1 = SplitRule.Create(TenantId, "Rule 1").Value;
        var rule2 = SplitRule.Create(TenantId, "Rule 2").Value;
        IReadOnlyList<SplitRule> rules = [rule1, rule2];

        _splitRuleRepository.GetPagedAsync(TenantId, 0, 20).Returns((rules, 2));

        var result = await _sut.ListAsync(TenantId, 1, 20);

        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEmpty_WhenNoRules()
    {
        IReadOnlyList<SplitRule> empty = [];
        _splitRuleRepository.GetPagedAsync(TenantId, 0, 20).Returns((empty, 0));

        var result = await _sut.ListAsync(TenantId, 1, 20);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    // --- DeactivateAsync tests ---

    [Fact]
    public async Task DeactivateAsync_ShouldSucceed_WhenRuleExists()
    {
        var ruleId = Guid.NewGuid();
        var rule = SplitRule.Create(TenantId, "My Rule").Value;

        _splitRuleRepository.GetByIdAsync(TenantId, ruleId).Returns(rule);

        await _sut.DeactivateAsync(TenantId, ruleId);

        rule.IsActive.Should().BeFalse();
        _splitRuleRepository.Received(1).Update(rule);
        await _splitRuleRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task DeactivateAsync_ShouldThrowNotFound_WhenRuleDoesNotExist()
    {
        var ruleId = Guid.NewGuid();
        _splitRuleRepository.GetByIdAsync(TenantId, ruleId).Returns((SplitRule?)null);

        var act = () => _sut.DeactivateAsync(TenantId, ruleId);

        await act.Should().ThrowAsync<NotFoundException>()
            .Where(e => e.Error.Code == "SplitRule.NotFound");
    }
}
