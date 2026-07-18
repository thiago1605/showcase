using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.ValueObjects;

public sealed class Money : ValueObject
{
    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Result<Money> Create(decimal amount, string currency = "BRL")
    {
        if (amount < 0)
            return Result.Failure<Money>(Error.Validation("Money.NegativeAmount", "O valor monetário não pode ser negativo."));

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
            return Result.Failure<Money>(Error.Validation("Money.InvalidCurrency", "A moeda deve ter exatamente 3 caracteres."));

        return Result.Success(new Money(amount, currency.ToUpperInvariant()));
    }

    public Money Add(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException($"Não é possível somar moedas diferentes: {Currency} e {other.Currency}.");
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException($"Não é possível subtrair moedas diferentes: {Currency} e {other.Currency}.");
        return new Money(Amount - other.Amount, Currency);
    }

    public bool IsGreaterThan(Money other) => Currency == other.Currency && Amount > other.Amount;
    public bool IsLessThan(Money other) => Currency == other.Currency && Amount < other.Amount;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    public override string ToString() => $"{Currency} {Amount:F2}";
}
