namespace FellowCore.Application.Common;

/// <summary>
/// Centralized rounding policy for BRL financial calculations.
/// All monetary amounts are rounded to 2 decimal places using MidpointRounding.ToEven (banker's rounding).
/// Reconciliation tolerance is 1 cent (R$0.01) to account for floating-point ↔ integer conversions.
/// </summary>
public static class RoundingPolicy
{
    public const int DecimalPlaces = 2;
    public const MidpointRounding Mode = MidpointRounding.ToEven;

    /// <summary>Reconciliation tolerance in cents (1 cent = R$0.01).</summary>
    public const long ToleranceCents = 1;

    /// <summary>Reconciliation tolerance in decimal (R$0.01).</summary>
    public const decimal ToleranceDecimal = 0.01m;

    /// <summary>Round a BRL amount to 2 decimal places using banker's rounding.</summary>
    public static decimal Round(decimal amount)
        => Math.Round(amount, DecimalPlaces, Mode);

    /// <summary>Convert decimal amount to cents (long), rounding first.</summary>
    public static long ToCents(decimal amount)
        => (long)(Round(amount) * 100);

    /// <summary>Convert cents to decimal amount.</summary>
    public static decimal FromCents(long cents)
        => cents / 100m;

    /// <summary>Check if two cent values are within tolerance.</summary>
    public static bool WithinTolerance(long expected, long actual)
        => Math.Abs(expected - actual) <= ToleranceCents;

    /// <summary>Calculate proportional amount (e.g., partial refund ratio). Rounds result.</summary>
    public static decimal Proportional(decimal total, decimal part, decimal fullAmount)
    {
        if (fullAmount == 0) return 0;
        return Round(total * part / fullAmount);
    }
}
