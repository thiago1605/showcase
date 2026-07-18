using FluentAssertions;
using FellowCore.Domain.ValueObjects;

namespace FellowCore.Domain.Tests.ValueObjects;

public class MoneyTests
{
    [Fact]
    public void Create_ShouldSucceed_WithValidAmount()
    {
        var result = Money.Create(100m);

        result.IsSuccess.Should().BeTrue();
        result.Value.Amount.Should().Be(100m);
        result.Value.Currency.Should().Be("BRL");
    }

    [Fact]
    public void Create_ShouldFail_WhenAmountIsNegative()
    {
        var result = Money.Create(-1m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Money.NegativeAmount");
    }

    [Theory]
    [InlineData("")]
    [InlineData("BR")]
    [InlineData("BRLX")]
    public void Create_ShouldFail_WhenCurrencyIsInvalid(string currency)
    {
        var result = Money.Create(100m, currency);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Money.InvalidCurrency");
    }

    [Fact]
    public void Add_ShouldSumAmounts()
    {
        var a = Money.Create(100m).Value;
        var b = Money.Create(50m).Value;

        var result = a.Add(b);

        result.Amount.Should().Be(150m);
    }

    [Fact]
    public void Subtract_ShouldReduceAmount()
    {
        var a = Money.Create(100m).Value;
        var b = Money.Create(30m).Value;

        var result = a.Subtract(b);

        result.Amount.Should().Be(70m);
    }

    [Fact]
    public void Equality_ShouldBeValueBased()
    {
        var a = Money.Create(100m, "BRL").Value;
        var b = Money.Create(100m, "BRL").Value;

        a.Should().Be(b);
    }

    [Fact]
    public void Equality_ShouldFail_WhenDifferentAmounts()
    {
        var a = Money.Create(100m).Value;
        var b = Money.Create(200m).Value;

        a.Should().NotBe(b);
    }
}
