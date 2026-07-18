namespace FellowCore.Domain.Primitives;

public sealed record Error(string Code, string Description)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    public static Error Validation(string code, string description) => new(code, description);
    public static Error NotFound(string code, string description) => new(code, description);
    public static Error Conflict(string code, string description) => new(code, description);
    public static Error Business(string code, string description) => new(code, description);
}
