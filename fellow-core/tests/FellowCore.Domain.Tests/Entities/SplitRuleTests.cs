using FluentAssertions;
using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Tests.Entities;

public class SplitRuleTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId1 = Guid.NewGuid();
    private static readonly Guid SellerId2 = Guid.NewGuid();
    private static readonly Guid SellerId3 = Guid.NewGuid();

    [Fact]
    public void Create_ShouldSucceed_WithValidData()
    {
        var result = SplitRule.Create(TenantId, "My Split Rule");

        result.IsSuccess.Should().BeTrue();
        result.Value.TenantId.Should().Be(TenantId);
        result.Value.Name.Should().Be("My Split Rule");
        // SplitRule.Create agora cria como draft (IsActive=false) — fluxo do portal
        // é: create → revisar destinatários → POST /split-rules/{id}/activate.
        result.Value.IsActive.Should().BeFalse();
        result.Value.Recipients.Should().BeEmpty();
    }

    [Fact]
    public void Create_ShouldFail_WhenTenantIdIsEmpty()
    {
        var result = SplitRule.Create(Guid.Empty, "Rule");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitRule.InvalidTenantId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_ShouldFail_WhenNameIsEmpty(string? name)
    {
        var result = SplitRule.Create(TenantId, name!);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitRule.InvalidName");
    }

    [Fact]
    public void Create_ShouldFail_WhenNameIsTooLong()
    {
        var longName = new string('A', 201);
        var result = SplitRule.Create(TenantId, longName);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitRule.NameTooLong");
    }

    [Fact]
    public void Create_ShouldTrimName()
    {
        var result = SplitRule.Create(TenantId, "  My Rule  ");

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("My Rule");
    }

    // --- AddRecipient tests ---

    [Fact]
    public void AddRecipient_ShouldSucceed_WithPercentage()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;

        var result = rule.AddRecipient(SellerId1, percentage: 50m, fixedAmount: null, priority: 1);

        result.IsSuccess.Should().BeTrue();
        rule.Recipients.Should().HaveCount(1);
        rule.Recipients.First().SellerId.Should().Be(SellerId1);
        rule.Recipients.First().Percentage.Should().Be(50m);
        rule.Recipients.First().FixedAmount.Should().BeNull();
        rule.Recipients.First().Priority.Should().Be(1);
    }

    [Fact]
    public void AddRecipient_ShouldSucceed_WithFixedAmount()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;

        var result = rule.AddRecipient(SellerId1, percentage: null, fixedAmount: 25m, priority: 0);

        result.IsSuccess.Should().BeTrue();
        rule.Recipients.Should().HaveCount(1);
        rule.Recipients.First().FixedAmount.Should().Be(25m);
        rule.Recipients.First().Percentage.Should().BeNull();
    }

    [Fact]
    public void AddRecipient_ShouldFail_WhenSellerIdIsEmpty()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;

        var result = rule.AddRecipient(Guid.Empty, percentage: 50m, fixedAmount: null, priority: 0);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitRule.InvalidSellerId");
    }

    [Fact]
    public void AddRecipient_ShouldFail_WhenNeitherPercentageNorFixedAmount()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;

        var result = rule.AddRecipient(SellerId1, percentage: null, fixedAmount: null, priority: 0);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitRule.NoAmountOrPercentage");
    }

    [Fact]
    public void AddRecipient_ShouldFail_WhenBothPercentageAndFixedAmount()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;

        var result = rule.AddRecipient(SellerId1, percentage: 50m, fixedAmount: 25m, priority: 0);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitRule.BothAmountAndPercentage");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100.01)]
    public void AddRecipient_ShouldFail_WhenPercentageIsInvalid(decimal percentage)
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;

        var result = rule.AddRecipient(SellerId1, percentage: percentage, fixedAmount: null, priority: 0);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitRule.InvalidPercentage");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void AddRecipient_ShouldFail_WhenFixedAmountIsZeroOrNegative(decimal fixedAmount)
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;

        var result = rule.AddRecipient(SellerId1, percentage: null, fixedAmount: fixedAmount, priority: 0);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitRule.InvalidFixedAmount");
    }

    [Fact]
    public void AddRecipient_ShouldAddMultipleRecipients()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;

        rule.AddRecipient(SellerId1, percentage: 30m, fixedAmount: null, priority: 1);
        rule.AddRecipient(SellerId2, percentage: 20m, fixedAmount: null, priority: 2);
        rule.AddRecipient(SellerId3, percentage: null, fixedAmount: 10m, priority: 3);

        rule.Recipients.Should().HaveCount(3);
    }

    // --- ValidateRecipientTotals tests ---

    [Fact]
    public void ValidateRecipientTotals_ShouldSucceed_WhenPercentageTotalsTo100()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;
        rule.AddRecipient(SellerId1, percentage: 60m, fixedAmount: null, priority: 1);
        rule.AddRecipient(SellerId2, percentage: 40m, fixedAmount: null, priority: 2);

        var result = rule.ValidateRecipientTotals();

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ValidateRecipientTotals_ShouldSucceed_WhenPercentageTotalsLessThan100()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;
        rule.AddRecipient(SellerId1, percentage: 30m, fixedAmount: null, priority: 1);
        rule.AddRecipient(SellerId2, percentage: 20m, fixedAmount: null, priority: 2);

        var result = rule.ValidateRecipientTotals();

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void ValidateRecipientTotals_ShouldFail_WhenPercentageExceeds100()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;
        rule.AddRecipient(SellerId1, percentage: 60m, fixedAmount: null, priority: 1);
        rule.AddRecipient(SellerId2, percentage: 50m, fixedAmount: null, priority: 2);

        var result = rule.ValidateRecipientTotals();

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SplitRule.PercentageExceeds100");
    }

    [Fact]
    public void ValidateRecipientTotals_ShouldSucceed_WithMixedPercentageAndFixed()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;
        rule.AddRecipient(SellerId1, percentage: 50m, fixedAmount: null, priority: 1);
        rule.AddRecipient(SellerId2, percentage: null, fixedAmount: 100m, priority: 2);

        var result = rule.ValidateRecipientTotals();

        result.IsSuccess.Should().BeTrue();
    }

    // --- Activate / Deactivate tests ---

    [Fact]
    public void Deactivate_ShouldSetIsActiveToFalse()
    {
        // Create-as-draft: precisa ativar primeiro pra exercitar o Deactivate.
        var rule = SplitRule.Create(TenantId, "Rule").Value;
        rule.Activate();
        rule.IsActive.Should().BeTrue();

        rule.Deactivate();

        rule.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_ShouldSetIsActiveToTrue()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;
        rule.Deactivate();
        rule.IsActive.Should().BeFalse();

        rule.Activate();

        rule.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Deactivate_ShouldUpdateTimestamp()
    {
        var rule = SplitRule.Create(TenantId, "Rule").Value;
        var originalUpdatedAt = rule.UpdatedAt;

        // Small delay to ensure timestamp differs
        Thread.Sleep(10);
        rule.Deactivate();

        rule.UpdatedAt.Should().BeOnOrAfter(originalUpdatedAt);
    }
}
