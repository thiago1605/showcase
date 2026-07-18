using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public interface INotificationOutboxProcessor
{
    Task ProcessAsync(CancellationToken ct = default);
}

/// <summary>
/// Worker assíncrono que processa o outbox de notificações.
///
/// Para cada mensagem due:
///   1. Cria <see cref="Notification"/> materializada (vira visible no
///      dropdown do seller via API).
///   2. Marca outbox message como ProcessedAt.
///   3. Em caso de exceção: RecordFailure com backoff exponencial.
///   4. Quando attempts &gt;= MaxAttempts: MarkDeadLetter (terminal,
///      ProcessedAt setado + LastError "[DLQ] ...").
///
/// Roda a cada 10s via Hangfire RecurringJob. Latência típica entre evento
/// e notificação aparecer no portal: ~5s média (depende de quando o evento
/// caiu na janela de polling).
///
/// Batch size 50 é conservador — em produção pode aumentar conforme volume.
/// Cada mensagem = 1 INSERT + 1 UPDATE, então 50 mensagens = ~100 statements
/// SQL por execução, OK.
/// </summary>
public class NotificationOutboxProcessor(
    INotificationOutboxRepository outboxRepository,
    INotificationRepository notificationRepository,
    AppDbContext context,
    IAppMetrics appMetrics,
    IRealtimeNotifier realtimeNotifier,
    ILogger<NotificationOutboxProcessor> logger
) : INotificationOutboxProcessor
{
    private const int BatchSize = 50;
    /// <summary>
    /// Total ~3h35min com backoff: 30s + 2min + 10min + 30min + 2h + 2h.
    /// Notificação é UX — se falhar 6x em 3h tem problema sistêmico maior
    /// que precisa de intervenção humana. DLQ ali até a investigação.
    /// </summary>
    private const int MaxAttempts = 6;

    public async Task ProcessAsync(CancellationToken ct = default)
    {
        var pending = await outboxRepository.GetDueAsync(BatchSize, DateTime.UtcNow);
        if (pending.Count == 0) return;

        logger.LogInformation(
            "[NOTIF_OUTBOX] Processando {Count} mensagens pendentes",
            pending.Count);

        int processed = 0, failed = 0, dlq = 0;
        // Sellers afetados nesse batch — usado pra invalidar a query do TanStack
        // via SignalR push após o SaveChanges. Set evita duplicar push pra um
        // mesmo seller que recebeu 2+ notificações no mesmo batch.
        var pushedSellerIds = new HashSet<Guid>();

        foreach (var msg in pending)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var notification = Notification.Create(
                    msg.TenantId,
                    msg.SellerId,
                    msg.Type,
                    msg.Title,
                    msg.Body,
                    msg.ResourceUrl,
                    msg.MetadataJson);
                notificationRepository.Add(notification);
                msg.MarkProcessed();
                outboxRepository.Update(msg);
                pushedSellerIds.Add(msg.SellerId);
                processed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[NOTIF_OUTBOX] Falha processando msg {MessageId} (attempt {Attempt})",
                    msg.Id, msg.Attempts + 1);

                // RecordFailure incrementa Attempts ANTES de decidir DLQ.
                // MaxAttempts=6 → na 6ª falha, Attempts vira 6 → DLQ.
                msg.RecordFailure(ex.Message);
                if (msg.Attempts >= MaxAttempts)
                {
                    msg.MarkDeadLetter(ex.Message);
                    dlq++;
                    logger.LogCritical(
                        "[NOTIF_OUTBOX_DLQ] Mensagem {MessageId} (type {Type}) excedeu {Max} tentativas. Última: {Error}",
                        msg.Id, msg.Type, MaxAttempts, ex.Message);
                }
                else
                {
                    failed++;
                }
                outboxRepository.Update(msg);
            }
        }

        // SaveChanges único no fim — uma transação engloba tudo do batch.
        // Trade-off: se SaveChanges falhar, perdemos o tracking dos retry counts
        // do batch inteiro. Aceitável pq na próxima rodada o GetDueAsync pega
        // os mesmos novamente (ProcessedAt continua null) — retry implícito.
        await notificationRepository.SaveChangesAsync();

        // Push SignalR — só APÓS o SaveChanges. Garante que se o client invalidar
        // a query e refetchar, vai achar a notification no banco (sem race).
        // 1 push por seller (mesmo se 5 notifs do mesmo seller no batch) — o
        // frontend invalida a query e refetcha 1x, pega todas.
        // Falha aqui é não-crítica — polling de 30s pega como fallback.
        foreach (var sellerId in pushedSellerIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await realtimeNotifier.SendToSellerAsync(
                    sellerId,
                    "notification.created",
                    new { sellerId });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "[NOTIF_OUTBOX] Falha no push SignalR pra seller {SellerId}. Polling 30s vai compensar.",
                    sellerId);
            }
        }

        // Métricas Prometheus — counters incrementam por batch, gauge reflete
        // backlog AGORA (depois do batch). Query do pending count usa partial
        // index, então é cheap mesmo com tabela grande.
        if (processed > 0) appMetrics.RecordNotificationOutboxProcessed(processed);
        if (failed > 0) appMetrics.RecordNotificationOutboxFailed(failed);
        if (dlq > 0) appMetrics.RecordNotificationOutboxDeadLetter(dlq);

        try
        {
            var remainingPending = await context.NotificationOutbox
                .Where(o => o.ProcessedAt == null)
                .CountAsync(ct);
            appMetrics.SetNotificationOutboxPending(remainingPending);
        }
        catch (Exception ex)
        {
            // Não-crítico — gauge fica com valor stale. Log e segue.
            logger.LogWarning(ex, "[NOTIF_OUTBOX] Falha ao atualizar gauge de pending");
        }

        logger.LogInformation(
            "[NOTIF_OUTBOX] Batch concluído. processed={Processed} failed={Failed} dlq={DLQ}",
            processed, failed, dlq);
    }
}
