using FellowCore.Domain.Primitives;

namespace FellowCore.Application.Exceptions;

public abstract class AppException(Error error) : Exception(error.Description)
{
    public Error Error { get; } = error;
}

public sealed class NotFoundException(Error error) : AppException(error)
{
    public NotFoundException(string code, string description)
        : this(Error.NotFound(code, description)) { }
}

public sealed class ConflictException(Error error) : AppException(error)
{
    public ConflictException(string code, string description)
        : this(Error.Conflict(code, description)) { }
}

public sealed class ValidationException(Error error) : AppException(error)
{
    public ValidationException(string code, string description)
        : this(Error.Validation(code, description)) { }
}

public sealed class BusinessException(Error error) : AppException(error)
{
    public BusinessException(string code, string description)
        : this(Error.Business(code, description)) { }
}

public sealed class UnauthorizedException(Error error) : AppException(error)
{
    public UnauthorizedException(string code, string description)
        : this(Error.Business(code, description)) { }
}

public sealed class PaymentProviderException(Error error) : AppException(error)
{
    public PaymentProviderException(string code, string description)
        : this(new Error(code, description)) { }
}

public sealed class ConfigurationException(Error error) : AppException(error)
{
    public ConfigurationException(string code, string description)
        : this(new Error(code, description)) { }
}

public sealed class ConcurrencyException(Error error) : AppException(error)
{
    public ConcurrencyException(string code, string description)
        : this(new Error(code, description)) { }
}

// --- Domain-specific exceptions (Woovi PIX + saques) ---

/// <summary>
/// PIX IN excedeu o limite operacional Woovi (R$ 800/TX). Lançada antes de
/// qualquer chamada ao provider — o customer não chega a receber QR code.
/// </summary>
public sealed class PixAmountLimitExceededException(decimal requested, decimal limit)
    : AppException(Error.Validation("Pix.AmountLimitExceeded",
        $"Valor R${requested:N2} excede o limite Woovi de R${limit:N2} por transacao PIX. Use cartao ou boleto."))
{
    public decimal RequestedAmount { get; } = requested;
    public decimal LimitAmount { get; } = limit;
}

/// <summary>Saque solicitado abaixo do mínimo (R$ 50).</summary>
public sealed class MinimumWithdrawException(decimal amount, decimal minimum)
    : AppException(Error.Validation("Withdraw.BelowMinimum",
        $"Saque de R${amount:N2} abaixo do minimo de R${minimum:N2}."))
{
    public decimal Amount { get; } = amount;
    public decimal Minimum { get; } = minimum;
}

/// <summary>Saque excede o teto individual configurado pro seller.</summary>
public sealed class IndividualWithdrawLimitException(decimal amount, decimal maxPerRequest)
    : AppException(Error.Validation("Withdraw.IndividualLimitExceeded",
        $"Saque de R${amount:N2} excede o teto de R${maxPerRequest:N2} por solicitacao."))
{
    public decimal Amount { get; } = amount;
    public decimal MaxPerRequest { get; } = maxPerRequest;
}

/// <summary>Saldo insuficiente na subconta pra o saque solicitado.</summary>
public sealed class InsufficientBalanceException(decimal amount, decimal available)
    : AppException(Error.Business("Withdraw.InsufficientBalance",
        $"Saldo R${available:N2} insuficiente para saque de R${amount:N2}."))
{
    public decimal Amount { get; } = amount;
    public decimal Available { get; } = available;
}

/// <summary>
/// Resposta da Woovi indicando que a feature (Subconta / Split) não está
/// ativada na conta da plataforma. Não é erro do cliente final — operações
/// devem solicitar ativação ao suporte Woovi (Morgana).
/// </summary>
public sealed class WooviFeatureDisabledException(string feature)
    : AppException(Error.Business("Woovi.FeatureDisabled",
        $"Feature Woovi '{feature}' nao ativada. Solicite ativacao via suporte (Morgana). Operacao bloqueada ate libera cao."))
{
    public string Feature { get; } = feature;
}
