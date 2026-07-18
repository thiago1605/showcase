using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Webhook endpoints — gerencia tanto webhooks tenant-wide (dev/integradores
/// recebendo TODOS os eventos do tenant) quanto producer-scoped (seller
/// recebendo só eventos das próprias TXs). A diferenciação é via SellerId
/// derivado do JWT:
/// - JWT com seller_id → request é producer-scoped (cria/lista só do próprio seller)
/// - JWT platform-operator ou ApiKey → tenant-wide (sem seller_id)
///
/// Decisão: reusamos o mesmo controller (em vez de criar /seller-webhooks
/// separado) porque a infra de probe, retry, deliveries, signature etc é
/// idêntica entre os dois escopos. A diferença é apenas o filtro por SellerId,
/// que cai naturalmente no GetCurrentSellerId(). Documentação OpenAPI separa
/// os dois casos via summary/remarks.
/// </summary>
[ApiController]
[Route("api/v1/webhook-endpoints")]
[AuthOrApiKeyAuth]
[EnableRateLimiting("fixed")]
public class WebhookEndpointsController(IWebhooksService webhooksService) : ControllerBase
{
    /// <summary>
    /// Cria um endpoint de webhook. Se o JWT tem seller_id, o endpoint é
    /// criado como producer-scoped (SellerId setado no DB). Caso contrário,
    /// é tenant-wide (SellerId NULL).
    /// </summary>
    [HttpPost]
    [AuditAction("webhook_endpoint.created")]
    public async Task<IActionResult> CreateEndpoint([FromBody] CreateWebhookEndpointDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        Guid? sellerId = HttpContext.GetCurrentSellerId();
        var result = await webhooksService.CreateEndpointAsync(tenantId, request, sellerId);
        return Created($"/api/v1/webhook-endpoints/{result.Id}", result);
    }

    /// <summary>
    /// Lista endpoints. Filtra automaticamente por escopo:
    /// - Seller-scoped JWT → apenas os endpoints do próprio seller
    /// - Platform/ApiKey → apenas tenant-wide (SellerId NULL)
    /// Pra ver todos (tenant-wide + dos sellers) chame /api/v1/admin/webhook-endpoints (futuro).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListEndpoints(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        Guid tenantId = HttpContext.GetTenantId();
        Guid? sellerId = HttpContext.GetCurrentSellerId();
        var result = await webhooksService.ListEndpointsByScopePagedAsync(tenantId, sellerId, page, pageSize);
        return Ok(result);
    }

    [HttpGet("{id:guid}/deliveries")]
    public async Task<IActionResult> GetDeliveries(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await webhooksService.GetDeliveriesAsync(tenantId, id, page, pageSize);
        return Ok(result);
    }

    [HttpPost("{id:guid}/deliveries/{deliveryId:guid}/retry")]
    [AuditAction("webhook_delivery.retried")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RetryDelivery(Guid id, Guid deliveryId)
    {
        Guid tenantId = HttpContext.GetTenantId();
        await webhooksService.RetryDeliveryAsync(tenantId, id, deliveryId);
        return NoContent();
    }

    [HttpGet("dead-letters")]
    public async Task<IActionResult> GetDeadLetters([FromQuery] int limit = 50)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await webhooksService.GetDeadLettersAsync(tenantId, limit);
        return Ok(result);
    }

    [HttpPost("dead-letters/retry-all")]
    [AuditAction("webhook_delivery.bulk_retried")]
    public async Task<IActionResult> RetryAllDeadLetters()
    {
        Guid tenantId = HttpContext.GetTenantId();
        var count = await webhooksService.RetryAllDeadLettersAsync(tenantId);
        return Ok(new { retriedCount = count });
    }

    [HttpDelete("{id:guid}")]
    [AuditAction("webhook_endpoint.deleted")]
    public async Task<IActionResult> DeleteEndpoint(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        await webhooksService.DeleteEndpointAsync(tenantId, id);
        return NoContent();
    }

    /// <summary>
    /// Rotaciona o segredo HMAC. Cutover atômico — exige que o seller já tenha atualizado
    /// o servidor dele pra aceitar o novo segredo (probe valida antes de persistir).
    /// O segredo em claro é devolvido **uma única vez** — depois disso só fica criptografado.
    /// </summary>
    [HttpPost("{id:guid}/rotate-secret")]
    [AuditAction("webhook_endpoint.secret_rotated")]
    public async Task<IActionResult> RotateSecret(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await webhooksService.RotateSecretAsync(tenantId, id);
        return Ok(result);
    }

    /// <summary>
    /// Envia um payload sintético assinado para o endpoint cadastrado e devolve o
    /// resultado bruto (status HTTP, latência, corpo). Não persiste WebhookDelivery —
    /// é só pra UI de "Enviar evento de teste".
    /// </summary>
    [HttpPost("{id:guid}/test")]
    [AuditAction("webhook_endpoint.tested")]
    public async Task<IActionResult> TestEndpoint(Guid id, [FromBody] TestWebhookEndpointDto? request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        string eventType = string.IsNullOrWhiteSpace(request?.EventType) ? "webhook.test" : request!.EventType;
        var result = await webhooksService.TestEndpointAsync(tenantId, id, eventType);
        return Ok(result);
    }
}
