using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.Webhooks.DTOs;

namespace FellowCore.Application.Modules.Webhooks.Interfaces;

public interface IWebhooksService
{
    Task HandleStripeEventAsync(StripeWebhookDto payload);
    Task HandleOpenPixEventAsync(OpenPixWebhookDto payload, string? authToken = null);

    /// <summary>
    /// Cria webhook endpoint. Quando <paramref name="sellerId"/> é setado, vira producer-scoped
    /// (dispara só pros eventos do seller). Quando null, é tenant-wide (legado — devs recebem tudo).
    /// </summary>
    Task<WebhookEndpointResponseDto> CreateEndpointAsync(Guid tenantId, CreateWebhookEndpointDto request, Guid? sellerId = null);

    Task<IEnumerable<WebhookEndpointResponseDto>> ListEndpointsAsync(Guid tenantId);

    /// <summary>
    /// Paginação tenant-wide (legado). Devolve TODOS os endpoints do tenant —
    /// incluindo producer-scoped — sem filtro. Use <see cref="ListEndpointsByScopePagedAsync"/>
    /// para o portal do producer (que precisa filtrar pelo próprio seller).
    /// </summary>
    Task<PagedResult<WebhookEndpointResponseDto>> ListEndpointsPagedAsync(Guid tenantId, int page, int pageSize);

    /// <summary>
    /// Paginação filtrada por escopo. <paramref name="sellerId"/> null = apenas
    /// tenant-wide; setado = apenas do seller. Usado pelo portal pra separar
    /// claramente os dois contextos de UI.
    /// </summary>
    Task<PagedResult<WebhookEndpointResponseDto>> ListEndpointsByScopePagedAsync(Guid tenantId, Guid? sellerId, int page, int pageSize);

    Task DeleteEndpointAsync(Guid tenantId, Guid endpointId);
    Task<PagedResult<WebhookDeliveryResponseDto>> GetDeliveriesAsync(Guid tenantId, Guid endpointId, int page, int pageSize);
    Task RetryDeliveryAsync(Guid tenantId, Guid endpointId, Guid deliveryId);
    Task<DeadLetterSummaryDto> GetDeadLettersAsync(Guid tenantId, int limit = 50);
    Task<int> RetryAllDeadLettersAsync(Guid tenantId);

    /// <summary>
    /// Envia payload sintético assinado para um endpoint cadastrado e retorna o resultado.
    /// Usado pra "Enviar evento de teste" na UI. Não persiste WebhookDelivery — é debug-only.
    /// </summary>
    Task<WebhookTestResultDto> TestEndpointAsync(Guid tenantId, Guid endpointId, string eventType);

    /// <summary>
    /// Verifica se uma URL responde 200 a um payload sintético assinado.
    /// Usado no fluxo de criação de endpoint pra rejeitar URLs que ainda não estão prontas.
    /// </summary>
    Task<WebhookTestResultDto> ProbeEndpointAsync(string url, string secret, string eventType, CancellationToken ct = default);

    /// <summary>
    /// Substitui o segredo HMAC do endpoint por um novo valor aleatório. Antes de
    /// persistir, faz probe no endpoint usando o **novo** secret — se o servidor do
    /// seller não validar a nova assinatura, retorna erro e nada muda. Isso protege
    /// contra rotação que deixaria o seller sem conseguir validar webhooks.
    /// O secret retornado em claro é a única chance do seller copiá-lo.
    /// </summary>
    Task<RotateWebhookSecretResultDto> RotateSecretAsync(Guid tenantId, Guid endpointId);
}
