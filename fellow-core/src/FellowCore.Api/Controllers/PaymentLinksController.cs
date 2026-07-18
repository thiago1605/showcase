using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Modules.PaymentLinks.DTOs;
using FellowCore.Application.Modules.PaymentLinks.Interfaces;
using FellowCore.Domain.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/payment-links")]
[EnableRateLimiting("fixed")]
public class PaymentLinksController(
    IPaymentLinkService paymentLinkService,
    ISplitRuleRepository splitRuleRepository) : ControllerBase
{
    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    [HttpPost]
    [AuthOrApiKeyAuth]
    [AuditAction("payment_link.created")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreatePaymentLinkDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();

        // JWT seller-scoped: forçar SellerId ao próprio (block se body tentar outro seller).
        var info = HttpContext.GetAuthInfo();
        if (info is { IsJwt: true, SellerId: { } scopedSellerId })
        {
            if (request.SellerId is { } bodySellerId && bodySellerId != scopedSellerId)
                return StatusCode(StatusCodes.Status403Forbidden);
            request = request with { SellerId = scopedSellerId };

            // Seller may only use split rules they own. Being a recipient of a rule does
            // not grant the right to apply it to a new sale — only the owner controls
            // when the rule fires. Domain-level validation (rule exists / IsActive)
            // happens inside the service.
            if (request.SplitRuleId is { } ruleId && ruleId != Guid.Empty)
            {
                var rule = await splitRuleRepository.GetByIdAsync(tenantId, ruleId);
                if (rule == null) return NotFound();
                if (rule.OwnerSellerId != scopedSellerId)
                    return StatusCode(StatusCodes.Status403Forbidden);
            }
        }

        var result = await paymentLinkService.CreateAsync(tenantId, request, BaseUrl);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpGet]
    [AuthOrApiKeyAuth]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        Guid tenantId = HttpContext.GetTenantId();

        // JWT seller-scoped: filtra no DB. ApiKey continua vendo o tenant inteiro.
        var info = HttpContext.GetAuthInfo();
        Guid? scopedSellerId = info is { IsJwt: true, SellerId: { } sid } ? sid : null;

        var all = await paymentLinkService.ListAsync(tenantId, BaseUrl, scopedSellerId);
        return Ok(all);
    }

    [HttpGet("{id:guid}")]
    [AuthOrApiKeyAuth]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await paymentLinkService.GetByIdAsync(tenantId, id, BaseUrl);
        if (this.EnforceOwnershipOr404(result.SellerId) is { } block) return block;
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [AuthOrApiKeyAuth]
    [AuditAction("payment_link.deleted")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await paymentLinkService.GetByIdAsync(tenantId, id, BaseUrl);
        if (this.EnforceOwnershipOr403(existing.SellerId) is { } block) return block;

        await paymentLinkService.DeactivateAsync(tenantId, id);
        return NoContent();
    }

    /// <summary>
    /// Atualiza campos não-financeiros (descrição, maxUses, expiresAt, splitRuleId).
    /// Amount e PaymentType permanecem imutáveis para preservar snapshots de
    /// transações já criadas pelo link.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [AuthOrApiKeyAuth]
    [AuditAction("payment_link.updated")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePaymentLinkDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await paymentLinkService.GetByIdAsync(tenantId, id, BaseUrl);
        if (this.EnforceOwnershipOr403(existing.SellerId) is { } block) return block;

        var result = await paymentLinkService.UpdateAsync(tenantId, id, request, BaseUrl);
        return Ok(result);
    }

    [HttpGet("pay/{token}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(string token)
    {
        var result = await paymentLinkService.ResolveAsync(token);
        return Ok(result);
    }

    [HttpPost("pay/{token}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Pay(string token, [FromBody] PayPaymentLinkDto request)
    {
        var result = await paymentLinkService.PayAsync(token, request);
        return Ok(result);
    }

    /// <summary>
    /// Public, anonymous polling endpoint. Used by the public checkout to track Pix
    /// (and any long-running rail) status without exposing the seller's auth-protected
    /// transaction endpoints. Tenant scope comes from the link's token.
    /// </summary>
    [HttpGet("pay/{token}/status/{transactionId:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Status(string token, Guid transactionId)
    {
        var result = await paymentLinkService.GetTransactionStatusAsync(token, transactionId);
        return Ok(result);
    }

    /// <summary>
    /// Opções de parcelamento (modo sem juros, seller absorve) pra este link.
    /// Lista vazia quando o link não aceita crédito ou o provider não suporta.
    /// </summary>
    [HttpGet("pay/{token}/installments")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Installments(string token)
    {
        var result = await paymentLinkService.GetInstallmentOptionsAsync(token);
        return Ok(result);
    }
}
