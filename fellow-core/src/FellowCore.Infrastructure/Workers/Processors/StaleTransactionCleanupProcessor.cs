using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public interface IStaleTransactionCleanupProcessor
{
    Task ProcessAsync(CancellationToken ct = default);
}

/// <summary>
/// Hangfire recurring job que cancela TXs zumbis — transações em CREATED ou
/// PROCESSING há mais tempo que o threshold do método de pagamento.
///
/// Por que zumbis aparecem:
///   - PI criado mas customer abandonou o checkout (cartão)
///   - PIX gerado mas customer nunca pagou (30min normal, 24h é zumbi)
///   - Boleto gerado mas customer nunca pagou no banco (3-5 dias)
///   - Smoke tests / tentativas de integração que travaram no meio
///
/// Sem esse cleanup, esses zumbis poluem indefinidamente:
///   - KPI "em andamento" infla (vimos R$ 31k de fantasma)
///   - Métricas de conversão ficam erradas (denominador inflado)
///   - Reconciliação Phase 7 acumula alertas falsos de "stuck"
///
/// Comportamento: chama <c>transaction.Cancel()</c> no domain (vai pra VOIDED).
/// VOIDED é um terminal state válido pra TXs nunca completadas — diferente de
/// FAILED (erro real) ou DECLINED (provider recusou). O semântico é "cancelei
/// porque ela não vai completar nunca".
/// </summary>
public class StaleTransactionCleanupProcessor(
    ITransactionRepository transactionRepository,
    IAppMetrics appMetrics,
    ILogger<StaleTransactionCleanupProcessor> logger) : IStaleTransactionCleanupProcessor
{
    // Thresholds atuais. Em produção devem vir de config (appsettings).
    // Os valores aqui são conservadores — preferimos esperar demais a cancelar
    // uma TX que ainda ia completar.
    private static readonly TimeSpan CardThreshold = TimeSpan.FromHours(24);
    private static readonly TimeSpan PixThreshold = TimeSpan.FromHours(24);
    private static readonly TimeSpan BoletoThreshold = TimeSpan.FromDays(7);

    public async Task ProcessAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var cardCutoff = now - CardThreshold;
        var pixCutoff = now - PixThreshold;
        var boletoCutoff = now - BoletoThreshold;

        // Bulk update via SQL — evita carregar entidades em memória, evita
        // problema xmin/Timeline (mesmo pattern do ApplyRefundAsync). Side-effect:
        // não emite TransactionEvent no Timeline pra cada zumbi. Aceitável
        // porque zumbis nunca tiveram trajetória de estado mesmo.
        int cancelled = await transactionRepository.BulkCancelStaleAsync(
            cardCutoff, pixCutoff, boletoCutoff);

        if (cancelled == 0)
        {
            logger.LogDebug("[STALE_CLEANUP] Nenhuma TX zumbi encontrada.");
            return;
        }

        appMetrics.RecordZombieCancellation(cancelled);

        logger.LogInformation(
            "[STALE_CLEANUP] {Count} TXs zumbis canceladas (VOIDED) — thresholds: card/pix {CardH}h, boleto {BolD}d.",
            cancelled, CardThreshold.TotalHours, BoletoThreshold.TotalDays);
    }
}
