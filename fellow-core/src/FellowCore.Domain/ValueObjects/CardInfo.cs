using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.ValueObjects;

public sealed class CardInfo : ValueObject
{
    public string First6 { get; private set; } = string.Empty;
    public string Last4 { get; private set; } = string.Empty;
    public string Brand { get; private set; } = string.Empty;
    public string HolderName { get; private set; } = string.Empty;
    public string Expiration { get; private set; } = string.Empty;

    private CardInfo() { } // EF Core

    private CardInfo(string first6, string last4, string brand, string holderName, string expiration)
    {
        First6 = first6;
        Last4 = last4;
        Brand = brand;
        HolderName = holderName;
        Expiration = expiration;
    }

    public static Result<CardInfo> Create(string? first6, string? last4, string? brand, string? holderName, string? expiration)
    {
        if (string.IsNullOrWhiteSpace(first6) || first6.Length != 6)
            return Result.Failure<CardInfo>(Error.Validation("CardInfo.InvalidFirst6", "Os primeiros 6 dígitos do cartão são obrigatórios."));

        if (string.IsNullOrWhiteSpace(last4) || last4.Length != 4)
            return Result.Failure<CardInfo>(Error.Validation("CardInfo.InvalidLast4", "Os últimos 4 dígitos do cartão são obrigatórios."));

        if (string.IsNullOrWhiteSpace(brand))
            return Result.Failure<CardInfo>(Error.Validation("CardInfo.MissingBrand", "A bandeira do cartão é obrigatória."));

        if (string.IsNullOrWhiteSpace(holderName))
            return Result.Failure<CardInfo>(Error.Validation("CardInfo.MissingHolderName", "O nome do titular é obrigatório."));

        if (string.IsNullOrWhiteSpace(expiration))
            return Result.Failure<CardInfo>(Error.Validation("CardInfo.MissingExpiration", "A validade do cartão é obrigatória."));

        return Result.Success(new CardInfo(first6, last4, brand, holderName.Trim().ToUpperInvariant(), expiration));
    }

    public string MaskedNumber => $"{First6}******{Last4}";

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return First6;
        yield return Last4;
        yield return Brand;
        yield return HolderName;
        yield return Expiration;
    }

    public override string ToString() => $"{Brand} {MaskedNumber}";
}
