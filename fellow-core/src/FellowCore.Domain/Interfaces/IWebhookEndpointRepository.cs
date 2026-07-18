using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IWebhookEndpointRepository
{
    /// <summary>
    /// Retorna todos os endpoints habilitados do tenant — inclui tenant-wide
    /// (SellerId IS NULL) e producer-scoped (SellerId NOT NULL). Caller filtra
    /// por SellerId/null conforme contexto do evento.
    /// </summary>
    Task<List<WebhookEndpoint>> GetActiveByTenantAsync(Guid tenantId);

    /// <summary>
    /// Retorna apenas endpoints tenant-wide habilitados (SellerId IS NULL).
    /// Usado em eventos de tenant-level que não têm seller atrelado.
    /// </summary>
    Task<List<WebhookEndpoint>> GetActiveTenantWideAsync(Guid tenantId);

    /// <summary>
    /// Retorna endpoints aplicáveis para um evento de uma TX de seller:
    /// tenant-wide (SellerId IS NULL) UNION endpoints do próprio seller
    /// (SellerId == sellerId). Ambos os grupos disparam.
    /// </summary>
    Task<List<WebhookEndpoint>> GetActiveForSellerEventAsync(Guid tenantId, Guid sellerId);

    /// <summary>
    /// Retorna apenas endpoints do seller (SellerId == sellerId, sem tenant-wide).
    /// Usado em listagem do portal do producer.
    /// </summary>
    Task<List<WebhookEndpoint>> GetBySellerAsync(Guid tenantId, Guid sellerId);

    Task<(IReadOnlyList<WebhookEndpoint> Items, int TotalCount)> GetPagedAsync(Guid tenantId, int skip, int take);

    /// <summary>
    /// Versão paginada que filtra por SellerId quando fornecido. Quando
    /// sellerId == null, devolve apenas tenant-wide; quando setado, devolve
    /// apenas os do seller (sem tenant-wide misturado — UI separa contextos).
    /// </summary>
    Task<(IReadOnlyList<WebhookEndpoint> Items, int TotalCount)> GetPagedByScopeAsync(Guid tenantId, Guid? sellerId, int skip, int take);

    Task<WebhookEndpoint?> GetByIdAsync(Guid tenantId, Guid endpointId);

    /// <summary>
    /// Retorna o endpoint habilitado (Enabled=true) com a URL fornecida no tenant
    /// e mesmo escopo de seller (incluindo null para tenant-wide), ou null se não
    /// existir. Usado pra impedir cadastro duplicado por escopo.
    /// </summary>
    Task<WebhookEndpoint?> GetEnabledByUrlAsync(Guid tenantId, string url, Guid? sellerId = null);

    void Add(WebhookEndpoint endpoint);
    void Update(WebhookEndpoint endpoint);
    Task SaveChangesAsync();
}
