using FluentAssertions;
using FellowCore.Application.Common;

namespace FellowCore.Application.Tests.Common;

public class RoundingPolicyTests
{
    [Theory]
    [InlineData(10.005, 10.00)]  // Banker's rounding: .005 rounds to even
    [InlineData(10.015, 10.02)]  // Banker's rounding: .015 rounds to even (up)
    [InlineData(10.125, 10.12)]  // Banker's rounding: .125 rounds to even (down)
    [InlineData(10.135, 10.14)]  // Banker's rounding: .135 rounds to even (up)
    [InlineData(99.999, 100.00)]
    [InlineData(0.001, 0.00)]
    public void Round_UsesBankersRounding(decimal input, decimal expected)
    {
        RoundingPolicy.Round(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(100.00, 10000)]
    [InlineData(0.01, 1)]
    [InlineData(99.99, 9999)]
    [InlineData(0.005, 0)]  // Rounds to 0.00, then 0 cents
    public void ToCents_ConvertsCorrectly(decimal amount, long expectedCents)
    {
        RoundingPolicy.ToCents(amount).Should().Be(expectedCents);
    }

    [Theory]
    [InlineData(10000, 100.00)]
    [InlineData(1, 0.01)]
    [InlineData(9999, 99.99)]
    public void FromCents_ConvertsCorrectly(long cents, decimal expected)
    {
        RoundingPolicy.FromCents(cents).Should().Be(expected);
    }

    [Theory]
    [InlineData(10000, 10000, true)]   // Exact match
    [InlineData(10000, 10001, true)]   // 1 cent diff = within tolerance
    [InlineData(10000, 9999, true)]    // 1 cent diff = within tolerance
    [InlineData(10000, 10002, false)]  // 2 cents diff = outside tolerance
    [InlineData(10000, 9998, false)]   // 2 cents diff = outside tolerance
    public void WithinTolerance_RespectsOneCentLimit(long expected, long actual, bool shouldBeWithin)
    {
        RoundingPolicy.WithinTolerance(expected, actual).Should().Be(shouldBeWithin);
    }

    [Fact]
    public void Proportional_CalculatesPartialRefundCorrectly()
    {
        // Refund 50 of 100, proportional of fee 10 = 5
        var result = RoundingPolicy.Proportional(10m, 50m, 100m);
        result.Should().Be(5m);
    }

    [Fact]
    public void Proportional_HandlesZeroDenominator()
    {
        var result = RoundingPolicy.Proportional(10m, 50m, 0m);
        result.Should().Be(0m);
    }

    [Fact]
    public void Proportional_RoundsResult()
    {
        // 10 * 33 / 100 = 3.30
        var result = RoundingPolicy.Proportional(10m, 33m, 100m);
        result.Should().Be(3.30m);
    }

    [Fact]
    public void ToleranceCents_IsOne()
    {
        RoundingPolicy.ToleranceCents.Should().Be(1);
    }

    [Fact]
    public void ToleranceDecimal_IsOneCent()
    {
        RoundingPolicy.ToleranceDecimal.Should().Be(0.01m);
    }
}
