using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.ValueObjects;

public sealed class Document : ValueObject
{
    public string Value { get; }
    public DocumentType Type { get; }

    private Document(string value, DocumentType type)
    {
        Value = value;
        Type = type;
    }

    public static Result<Document> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<Document>(Error.Validation("Document.Empty", "O documento não pode ser vazio."));

        var digits = new string(value.Where(char.IsDigit).ToArray());

        if (digits.Length == 11)
        {
            if (!IsValidCpf(digits))
                return Result.Failure<Document>(Error.Validation("Document.InvalidCpf", "CPF inválido."));
            return Result.Success(new Document(digits, DocumentType.CPF));
        }

        if (digits.Length == 14)
        {
            if (!IsValidCnpj(digits))
                return Result.Failure<Document>(Error.Validation("Document.InvalidCnpj", "CNPJ inválido."));
            return Result.Success(new Document(digits, DocumentType.CNPJ));
        }

        return Result.Failure<Document>(Error.Validation("Document.InvalidLength", "O documento deve ter 11 dígitos (CPF) ou 14 dígitos (CNPJ)."));
    }

    private static bool IsValidCpf(string digits)
    {
        if (digits.Distinct().Count() == 1) return false;

        var sum = 0;
        for (var i = 0; i < 9; i++) sum += int.Parse(digits[i].ToString()) * (10 - i);
        var remainder = sum % 11;
        var d1 = remainder < 2 ? 0 : 11 - remainder;
        if (d1 != int.Parse(digits[9].ToString())) return false;

        sum = 0;
        for (var i = 0; i < 10; i++) sum += int.Parse(digits[i].ToString()) * (11 - i);
        remainder = sum % 11;
        var d2 = remainder < 2 ? 0 : 11 - remainder;
        return d2 == int.Parse(digits[10].ToString());
    }

    private static bool IsValidCnpj(string digits)
    {
        if (digits.Distinct().Count() == 1) return false;

        int[] weights1 = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int[] weights2 = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

        var sum = weights1.Select((w, i) => w * int.Parse(digits[i].ToString())).Sum();
        var remainder = sum % 11;
        var d1 = remainder < 2 ? 0 : 11 - remainder;
        if (d1 != int.Parse(digits[12].ToString())) return false;

        sum = weights2.Select((w, i) => w * int.Parse(digits[i].ToString())).Sum();
        remainder = sum % 11;
        var d2 = remainder < 2 ? 0 : 11 - remainder;
        return d2 == int.Parse(digits[13].ToString());
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => Value;
}

public enum DocumentType { CPF, CNPJ }
