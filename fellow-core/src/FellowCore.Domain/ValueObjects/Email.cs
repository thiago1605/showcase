using System.Text.RegularExpressions;
using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.ValueObjects;

public sealed class Email : ValueObject
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));

    public string Value { get; }

    private Email(string value) => Value = value;

    public static Result<Email> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<Email>(Error.Validation("Email.Empty", "O e-mail não pode ser vazio."));

        if (!EmailRegex.IsMatch(value))
            return Result.Failure<Email>(Error.Validation("Email.InvalidFormat", "O formato do e-mail é inválido."));

        return Result.Success(new Email(value.ToLowerInvariant()));
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}
