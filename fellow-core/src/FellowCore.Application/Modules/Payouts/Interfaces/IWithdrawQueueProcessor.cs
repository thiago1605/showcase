namespace FellowCore.Application.Modules.Payouts.Interfaces;

/// <summary>
/// Processador Hangfire que esvazia a fila FIFO de saques agendados
/// (Payouts com Status=PENDING e ScheduledFor &lt;= now). Roda a cada 5 minutos.
///
/// Comportamento: pega lote em ordem de criação, valida cap diário pra cada um
/// e executa via <see cref="IPayoutProcessor"/>. Saques que ainda excedem cap
/// continuam na fila (próxima execução tenta de novo após renovar o cap no
/// 0:00 UTC OU quando outro saque do dia falhar/cancelar liberando espaço).
/// </summary>
public interface IWithdrawQueueProcessor
{
    Task ProcessAsync(CancellationToken ct = default);
}
