using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

/// <summary>
/// Repositório de notificações in-app. Sempre escopado por SellerId (e
/// TenantId como defesa em profundidade). Lista ordenada por CreatedAt desc
/// — notificações mais novas primeiro.
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Lista paginada de notificações do seller. <paramref name="unreadOnly"/>
    /// quando true filtra por <c>ReadAt IS NULL</c>. Retorna total
    /// (sem o limit/offset) pra paginação no frontend.
    /// </summary>
    Task<(IReadOnlyList<Notification> Items, int TotalCount)> GetPagedAsync(
        Guid tenantId,
        Guid sellerId,
        int skip,
        int take,
        bool unreadOnly = false);

    /// <summary>
    /// Conta notificações não lidas do seller. Cheap query — só um COUNT
    /// com WHERE ReadAt IS NULL + index. Chamada pelo polling do badge.
    /// </summary>
    Task<int> GetUnreadCountAsync(Guid tenantId, Guid sellerId);

    /// <summary>
    /// Busca uma notificação por Id, escopada por tenant+seller — null se
    /// não pertence ao seller (evita IDOR onde seller A marca notificação
    /// de B como lida).
    /// </summary>
    Task<Notification?> GetByIdAsync(Guid tenantId, Guid sellerId, Guid notificationId);

    /// <summary>
    /// Marca todas as não lidas do seller como lidas (bulk update). Mais
    /// eficiente que iterar e Update() — usa ExecuteUpdateAsync (EF 7+).
    /// Retorna o número de registros afetados.
    /// </summary>
    Task<int> MarkAllReadAsync(Guid tenantId, Guid sellerId);

    void Add(Notification notification);
    void Update(Notification notification);
    Task SaveChangesAsync();
}
