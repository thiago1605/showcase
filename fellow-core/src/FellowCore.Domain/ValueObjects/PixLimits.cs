namespace FellowCore.Domain.ValueObjects;

/// <summary>
/// Limites operacionais do PIX impostos pelo provider Woovi/OpenPix —
/// confirmados pelo suporte oficial (Morgana Miller, 2026-05-15).
///
/// Quando o produto migrar pra outro provider PIX ou Woovi liberar limites
/// diferentes em produção, atualize aqui — o resto do código consome essas
/// constantes via specifications/guards.
/// </summary>
public static class PixLimits
{
    /// <summary>Valor máximo por TX PIX IN aceito pela conta Woovi (R$ 800).</summary>
    public const decimal MaxAmountPerTransactionInbound = 800m;

    /// <summary>Cap diário total de saques PIX OUT na janela 8h–20:59 (R$ 48.800).</summary>
    public const decimal DailyOutboundLimitBusinessHours = 48_800m;

    /// <summary>Cap noturno 21h–7:59 e fins de semana (R$ 10.000).</summary>
    public const decimal DailyOutboundLimitOffHours = 10_000m;

    /// <summary>
    /// True se o valor solicitado em PIX IN é aceitável pelo provider.
    /// Caller-responsibility: lançar <c>PixAmountLimitExceededException</c>
    /// quando false, com mensagem clara pro usuário.
    /// </summary>
    public static bool IsInboundAmountAllowed(decimal amount)
        => amount > 0 && amount <= MaxAmountPerTransactionInbound;
}

/// <summary>
/// Constantes de saque (D+0/D+1, fees, mínimos). Origem: spec comercial 2026-05-15.
/// </summary>
public static class WithdrawRules
{
    /// <summary>Saque mínimo aceito (R$ 50).</summary>
    public const decimal MinimumAmount = 50m;

    /// <summary>Default máximo por solicitação pra sellers novos (R$ 5.000).</summary>
    public const decimal DefaultMaxPerRequest = 5_000m;

    /// <summary>Saque abaixo deste valor paga tarifa fixa (R$ 1,00).</summary>
    public const decimal SmallWithdrawThreshold = 500m;

    /// <summary>Tarifa fixa cobrada quando o saque é < <c>SmallWithdrawThreshold</c>.</summary>
    public const decimal SmallWithdrawFee = 1m;

    /// <summary>Taxa de antecipação D+0 sobre o valor (1%).</summary>
    public const decimal D0FeePercent = 0.01m;
}
