using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Payouts.Interfaces;

/// <summary>
/// Saque ("withdraw") na nomenclatura comercial = facade sobre <see cref="IPayoutService"/>
/// que aplica as guards da spec 2026 (min R$ 50, max per-seller, cap diário Woovi R$ 48.800,
/// fees R$ 1 / 1%) e decide entre execução imediata (D+0) e agendamento (D+1 ou fila quando
/// excede cap). O resultado final é sempre persistido como <c>Payout</c>.
/// </summary>
public interface IWithdrawService
{
    /// <summary>
    /// Solicita um saque. Pode retornar tanto resultado imediato (executado agora)
    /// quanto agendado (na fila pra próximo dia útil ou pra esvaziamento de cap).
    /// Erros de domínio são lançados como exceptions específicas
    /// (<c>MinimumWithdrawException</c>, <c>IndividualWithdrawLimitException</c>,
    /// <c>InsufficientBalanceException</c>).
    /// </summary>
    Task<WithdrawResult> RequestAsync(
        Guid tenantId,
        Guid sellerId,
        decimal amount,
        WithdrawType type,
        CancellationToken ct = default);
}

/// <summary>
/// Snapshot do resultado de um pedido de saque — usado pelo controller pra montar
/// a resposta sem expor a entidade Payout inteira.
/// </summary>
public sealed record WithdrawResult(
    Guid PayoutId,
    PayoutStatus Status,
    decimal GrossAmount,
    decimal Fee,
    decimal NetAmount,
    WithdrawType Type,
    DateTime? ScheduledFor,
    string Message);
