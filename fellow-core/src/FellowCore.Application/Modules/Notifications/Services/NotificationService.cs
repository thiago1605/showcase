using System.Text.Json;
using FellowCore.Application.Modules.Notifications.DTOs;
using FellowCore.Application.Modules.Notifications.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Notifications.Services;

public class NotificationService(
    INotificationRepository repository,
    INotificationOutboxRepository outboxRepository,
    ILogger<NotificationService> logger
) : INotificationService
{
    // Label PT-BR pros tiers — keep em sync com TierBadge no frontend.
    // Diamond é o display label do enum DIAMOND (que existe como int no DB,
    // antigo PLATINUM).
    private static readonly IReadOnlyDictionary<SellerTier, string> TierLabel =
        new Dictionary<SellerTier, string>
        {
            [SellerTier.SILVER] = "Silver",
            [SellerTier.GOLD] = "Gold",
            [SellerTier.DIAMOND] = "Diamond",
            [SellerTier.BLACK] = "Black",
            [SellerTier.INFINITE] = "Infinite",
        };

    public async Task<NotificationListDto> ListAsync(
        Guid tenantId,
        Guid sellerId,
        int page = 1,
        int pageSize = 20,
        bool unreadOnly = false)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;
        var skip = (page - 1) * pageSize;

        var (items, totalCount) = await repository.GetPagedAsync(
            tenantId, sellerId, skip, pageSize, unreadOnly);
        // Unread count é sempre o GLOBAL (independente do filtro da página) —
        // serve pro badge no header que reflete o estado atual da conta.
        var unreadCount = unreadOnly
            ? totalCount
            : await repository.GetUnreadCountAsync(tenantId, sellerId);

        var dtos = items.Select(ToDto).ToList();
        return new NotificationListDto(dtos, totalCount, unreadCount);
    }

    public Task<int> GetUnreadCountAsync(Guid tenantId, Guid sellerId)
        => repository.GetUnreadCountAsync(tenantId, sellerId);

    public async Task<bool> MarkReadAsync(Guid tenantId, Guid sellerId, Guid notificationId)
    {
        var notification = await repository.GetByIdAsync(tenantId, sellerId, notificationId);
        if (notification is null) return false;
        var changed = notification.MarkRead();
        if (changed)
        {
            repository.Update(notification);
            await repository.SaveChangesAsync();
        }
        return true;
    }

    public Task<int> MarkAllReadAsync(Guid tenantId, Guid sellerId)
        => repository.MarkAllReadAsync(tenantId, sellerId);

    public Task CreateAsync(
        Guid tenantId,
        Guid sellerId,
        NotificationType type,
        string title,
        string body,
        string? resourceUrl = null,
        object? metadata = null)
    {
        // Outbox pattern: enfileira INTENT de notificação na transaction da
        // operação principal (captura, tier change, etc.) — atomicidade
        // garantida sem bloquear o caller. NotificationOutboxProcessor
        // (recurring job Hangfire) materializa em Notification depois.
        //
        // NÃO chamamos SaveChangesAsync aqui — o IUnitOfWork do caller commita
        // junto. Se a operação principal rolar back, o outbox message some
        // junto (atomicidade outbox pattern). Por isso CreateAsync virou
        // síncrono internamente (Task.CompletedTask) — é só Add no change tracker.
        try
        {
            var metadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata);
            var outboxMsg = NotificationOutbox.Create(
                tenantId, sellerId, type, title, body, resourceUrl, metadataJson);
            outboxRepository.Add(outboxMsg);
        }
        catch (Exception ex)
        {
            // Producers NUNCA quebram o fluxo principal — só loggam. Falha aqui
            // é improvável (só Add no DbSet + Create() validation) mas defendemos
            // por garantia. A captura/tier/payout segue mesmo se enfileirar falhar.
            logger.LogError(ex,
                "Falha ao enfileirar notificação ({Type}) pra seller {SellerId}",
                type, sellerId);
        }
        return Task.CompletedTask;
    }

    public Task NotifyTransactionCapturedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid transactionId,
        decimal amountBrl,
        string paymentMethodLabel)
    {
        var formatted = FormatBrl(amountBrl);
        return CreateAsync(
            tenantId, sellerId,
            NotificationType.TRANSACTION_CAPTURED,
            title: $"Pagamento recebido — {formatted}",
            body: $"Captura confirmada via {paymentMethodLabel}.",
            resourceUrl: $"/transactions/{transactionId}",
            metadata: new { transactionId, amountBrl, paymentMethodLabel });
    }

    public Task NotifyTierChangedAsync(
        Guid tenantId,
        Guid sellerId,
        SellerTier from,
        SellerTier to)
    {
        var fromLabel = TierLabel[from];
        var toLabel = TierLabel[to];
        var isUpgrade = (int)to > (int)from;
        return CreateAsync(
            tenantId, sellerId,
            type: isUpgrade ? NotificationType.TIER_UPGRADED : NotificationType.TIER_DOWNGRADED,
            title: isUpgrade
                ? $"Você subiu para {toLabel}!"
                : $"Você voltou para {toLabel}",
            body: isUpgrade
                ? $"Parabéns — suas taxas agora são as do nível {toLabel}."
                : $"Seu nível mudou de {fromLabel} para {toLabel}. Confira as novas taxas.",
            resourceUrl: "/tier",
            metadata: new { from = fromLabel, to = toLabel, isUpgrade });
    }

    public Task NotifyDisputeOpenedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid transactionId,
        decimal amountBrl,
        string? reason)
    {
        var formatted = FormatBrl(amountBrl);
        var hasReason = !string.IsNullOrWhiteSpace(reason);
        return CreateAsync(
            tenantId, sellerId,
            NotificationType.DISPUTE_OPENED,
            title: $"Contestação aberta — {formatted}",
            body: hasReason
                ? $"O cliente abriu uma contestação. Motivo: {reason}. Acompanhe os detalhes pra responder a tempo."
                : "O cliente abriu uma contestação. Acompanhe os detalhes pra responder a tempo.",
            resourceUrl: $"/transactions/{transactionId}",
            metadata: new { transactionId, amountBrl, reason });
    }

    public Task NotifyDisputeResolvedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid transactionId,
        decimal amountBrl,
        bool won)
    {
        var formatted = FormatBrl(amountBrl);
        return CreateAsync(
            tenantId, sellerId,
            NotificationType.DISPUTE_RESOLVED,
            title: won
                ? $"Contestação resolvida a seu favor — {formatted}"
                : $"Contestação perdida — {formatted}",
            body: won
                ? "O valor permanece capturado. Sem ação adicional necessária."
                : "O valor foi estornado ao cliente. A taxa de chargeback aparece no seu próximo extrato.",
            resourceUrl: $"/transactions/{transactionId}",
            metadata: new { transactionId, amountBrl, won });
    }

    public Task NotifyPayoutCompletedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid payoutId,
        decimal amountBrl)
    {
        var formatted = FormatBrl(amountBrl);
        return CreateAsync(
            tenantId, sellerId,
            NotificationType.PAYOUT_COMPLETED,
            title: "Saque concluído",
            body: $"{formatted} transferidos pra sua conta bancária.",
            resourceUrl: $"/payouts/{payoutId}",
            metadata: new { payoutId, amountBrl });
    }

    public Task NotifyPayoutFailedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid payoutId,
        decimal amountBrl,
        string? reason)
    {
        var formatted = FormatBrl(amountBrl);
        return CreateAsync(
            tenantId, sellerId,
            NotificationType.PAYOUT_FAILED,
            title: $"Saque falhou — {formatted}",
            body: string.IsNullOrWhiteSpace(reason)
                ? "Não foi possível concluir o saque. Verifique os dados bancários e tente novamente."
                : $"Não foi possível concluir o saque. Motivo: {reason}",
            resourceUrl: $"/payouts/{payoutId}",
            metadata: new { payoutId, amountBrl, reason });
    }

    public Task NotifyRefundCompletedAsync(
        Guid tenantId,
        Guid sellerId,
        Guid transactionId,
        decimal amountBrl)
    {
        var formatted = FormatBrl(amountBrl);
        return CreateAsync(
            tenantId, sellerId,
            NotificationType.TRANSACTION_REFUNDED,
            title: $"Reembolso concluído — {formatted}",
            body: "O valor foi devolvido ao cliente. Consulte o detalhe da transação pra ver a quebra.",
            resourceUrl: $"/transactions/{transactionId}",
            metadata: new { transactionId, amountBrl });
    }

    public Task NotifyWebhookDeliveryFailedAsync(
        Guid tenantId,
        Guid sellerId,
        string endpointUrl,
        bool attemptsExhausted)
    {
        var truncatedUrl = endpointUrl.Length > 80 ? endpointUrl[..80] + "…" : endpointUrl;
        return CreateAsync(
            tenantId, sellerId,
            NotificationType.WEBHOOK_DELIVERY_FAILED,
            title: attemptsExhausted
                ? "Webhook desabilitado por falhas repetidas"
                : "Falha na entrega do webhook",
            body: attemptsExhausted
                ? $"O endpoint {truncatedUrl} falhou múltiplas tentativas e foi pausado. Verifique a configuração e reative manualmente."
                : $"Não foi possível entregar um webhook em {truncatedUrl}. Vamos tentar de novo automaticamente.",
            resourceUrl: "/webhooks",
            metadata: new { endpointUrl, attemptsExhausted });
    }

    public Task NotifyAffiliationRequestedAsync(
        Guid tenantId, Guid producerSellerId, Guid affiliationId, string productName, string affiliateName)
        => CreateAsync(
            tenantId, producerSellerId,
            NotificationType.AFFILIATION_REQUESTED,
            title: "Nova solicitação de afiliação",
            body: $"{affiliateName} quer promover \"{productName}\". Aprove ou recuse pra liberar o link de tracking.",
            resourceUrl: $"/products?affiliation={affiliationId}",
            metadata: new { affiliationId, productName, affiliateName });

    public Task NotifyAffiliationApprovedAsync(
        Guid tenantId, Guid affiliateSellerId, Guid affiliationId, string productName)
        => CreateAsync(
            tenantId, affiliateSellerId,
            NotificationType.AFFILIATION_APPROVED,
            title: "Afiliação aprovada",
            body: $"Você foi aprovado pra promover \"{productName}\". O link de tracking já está ativo.",
            resourceUrl: $"/affiliations?id={affiliationId}",
            metadata: new { affiliationId, productName });

    public Task NotifyAffiliationRejectedAsync(
        Guid tenantId, Guid affiliateSellerId, Guid affiliationId, string productName, string? reason)
        => CreateAsync(
            tenantId, affiliateSellerId,
            NotificationType.AFFILIATION_REJECTED,
            title: "Afiliação recusada",
            body: string.IsNullOrWhiteSpace(reason)
                ? $"Seu pedido pra promover \"{productName}\" não foi aprovado."
                : $"Seu pedido pra promover \"{productName}\" não foi aprovado. Motivo: {reason}",
            resourceUrl: $"/affiliations?id={affiliationId}",
            metadata: new { affiliationId, productName, reason });

    private static string FormatBrl(decimal value) =>
        value.ToString("C", new System.Globalization.CultureInfo("pt-BR"));

    private static NotificationDto ToDto(Notification n)
    {
        object? metadata = null;
        if (!string.IsNullOrEmpty(n.MetadataJson))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<JsonElement>(n.MetadataJson);
            }
            catch
            {
                // Se metadata corrupto, devolve como string mesmo — não bloqueia
                // a notificação. Improvável dado que entra via JsonSerializer.
                metadata = n.MetadataJson;
            }
        }
        return new NotificationDto(
            n.Id,
            (int)n.Type,
            n.Title,
            n.Body,
            n.ResourceUrl,
            metadata,
            n.ReadAt,
            n.CreatedAt);
    }
}
