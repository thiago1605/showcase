using FellowCore.Application.Modules.Notifications.DTOs;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Notifications.Interfaces;

/// <summary>
/// Service de notificações in-app. Tem duas faces:
///  - **Producer**: métodos `Notify*Async` chamados pelo resto do código
///    (webhooks, jobs, services) pra criar notificações em eventos relevantes.
///    Cada método monta o título/body padrão pro tipo.
///  - **Consumer**: métodos de leitura/marcação chamados pelo controller
///    que serve o frontend.
///
/// Producers usam fire-and-forget no caller (await SaveChangesAsync) — falha
/// na criação de notificação NÃO deve quebrar o fluxo principal (captura,
/// payout, etc.). Cada producer loga erro mas não propaga.
/// </summary>
public interface INotificationService
{
    // ---- Reads (chamados pelo controller) ----

    /// <summary>
    /// Lista paginada das notificações do seller logado. Inclui também o
    /// unread count total (mesmo que a page traga só algumas — útil pro badge).
    /// </summary>
    Task<NotificationListDto> ListAsync(
        Guid tenantId,
        Guid sellerId,
        int page = 1,
        int pageSize = 20,
        bool unreadOnly = false);

    Task<int> GetUnreadCountAsync(Guid tenantId, Guid sellerId);

    /// <summary>
    /// Marca uma notificação como lida. Idempotente. Retorna false se a
    /// notificação não foi encontrada OU não pertence ao seller (mesmo
    /// status code pra evitar IDOR enumeration).
    /// </summary>
    Task<bool> MarkReadAsync(Guid tenantId, Guid sellerId, Guid notificationId);

    /// <summary>
    /// Bulk: marca todas as não lidas do seller como lidas. Retorna quantas
    /// foram afetadas.
    /// </summary>
    Task<int> MarkAllReadAsync(Guid tenantId, Guid sellerId);

    // ---- Producers (chamados por outros services/jobs) ----

    /// <summary>
    /// Genérico — usado por producers de mais alto nível ou por casos custom
    /// (anúncios manuais via admin, p.ex.). Prefira os helpers tipados abaixo
    /// quando o evento for conhecido.
    /// </summary>
    Task CreateAsync(
        Guid tenantId,
        Guid sellerId,
        NotificationType type,
        string title,
        string body,
        string? resourceUrl = null,
        object? metadata = null);

    Task NotifyTransactionCapturedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid transactionId,
        decimal amountBrl,
        string paymentMethodLabel);

    Task NotifyTierChangedAsync(
        Guid tenantId,
        Guid sellerId,
        SellerTier from,
        SellerTier to);

    Task NotifyDisputeOpenedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid transactionId,
        decimal amountBrl,
        string? reason);

    /// <param name="won">true = seller venceu o dispute (verde); false = perdeu (vermelho).</param>
    Task NotifyDisputeResolvedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid transactionId,
        decimal amountBrl,
        bool won);

    Task NotifyPayoutCompletedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid payoutId,
        decimal amountBrl);

    Task NotifyPayoutFailedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid payoutId,
        decimal amountBrl,
        string? reason);

    Task NotifyRefundCompletedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid transactionId,
        decimal amountBrl);

    /// <param name="endpointUrl">URL do endpoint que falhou (truncado se muito longo).</param>
    /// <param name="attemptsExhausted">true se já esgotou retries (DLQ-like) — body muda pra urgir ação.</param>
    /// <remarks>
    /// Sprint 2: NÃO plugado em producer real ainda. WebhookEndpoint é tenant-scoped
    /// (não tem SellerId), então não temos como rotear pra um seller específico.
    /// O <c>WebhookRetryProcessor</c> já manda alerta por email pro operador via
    /// <c>SendDlqAlertAsync</c>, cobrindo o caso. Quando endpoints virarem
    /// seller-scoped (futuro), este producer ganha plug nesse processor.
    /// </remarks>
    Task NotifyWebhookDeliveryFailedAsync(
        Guid tenantId,
        Guid sellerId,
        string endpointUrl,
        bool attemptsExhausted);

    // ---- Marketplace (Sprint 3) ----

    /// <summary>Para o produtor: novo afiliado solicitou afiliação (modo REQUEST).</summary>
    Task NotifyAffiliationRequestedAsync(
        Guid tenantId, Guid producerSellerId, Guid affiliationId, string productName, string affiliateName);

    /// <summary>Para o afiliado: o produtor aprovou seu pedido.</summary>
    Task NotifyAffiliationApprovedAsync(
        Guid tenantId, Guid affiliateSellerId, Guid affiliationId, string productName);

    /// <summary>Para o afiliado: o produtor rejeitou seu pedido.</summary>
    Task NotifyAffiliationRejectedAsync(
        Guid tenantId, Guid affiliateSellerId, Guid affiliationId, string productName, string? reason);
}
