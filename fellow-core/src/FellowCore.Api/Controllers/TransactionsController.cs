using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Enums;
using FellowCore.Infrastructure.Workers.Processors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/transactions")]
[EnableRateLimiting("fixed")]
[AuthOrApiKeyAuth]
public class TransactionsController(
    ITransactionService transactionService,
    IExportService exportService,
    IStaleTransactionCleanupProcessor staleCleanup) : ControllerBase
{
    /// <summary>
    /// Dispara o cleanup de TXs zumbis ad-hoc — útil pra zerar "em andamento"
    /// fantasma sem esperar o cron (rodeia de hora em hora). Idempotente.
    /// </summary>
    [HttpPost("cleanup-stale")]
    [AuditAction("transactions.cleanup_stale")]
    public async Task<IActionResult> CleanupStale()
    {
        await staleCleanup.ProcessAsync(HttpContext.RequestAborted);
        return Ok(new { triggered = true });
    }

    /// <summary>
    /// HARD DELETE de TXs smoke (DECLINED/VOIDED/FAILED com RefundedAmount=0
    /// e zero filhas em fluxos de dinheiro real). Útil pra limpar lixo de
    /// testes/abandonos que infla KPIs. Escopo: por tenant da chave/JWT.
    /// </summary>
    [HttpPost("cleanup-smoke")]
    [AuditAction("transactions.cleanup_smoke")]
    public async Task<IActionResult> CleanupSmoke([FromServices] FellowCore.Domain.Interfaces.ITransactionRepository txRepo)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await txRepo.DeleteSmokeAsync(tenantId);
        return Ok(new { deletedCount = result.DeletedCount, deletedAmount = result.DeletedAmount });
    }

    /// <summary>
    /// HARD DELETE de "fake captures" — TXs marcadas localmente como CAPTURED
    /// mas que NÃO existem como charge real no Stripe (PI sem latest_charge,
    /// ou PI inexistente). Tipicamente seed data manipulado via SQL ou
    /// smoke tests que manualmente setaram Status=3 sem confirmação real.
    /// Cada TX é verificada contra Stripe — sem matches, sem delete.
    /// </summary>
    [HttpPost("cleanup-orphan-captures")]
    [AuditAction("transactions.cleanup_orphan_captures")]
    public async Task<IActionResult> CleanupOrphanCaptures(
        [FromServices] FellowCore.Domain.Interfaces.ITransactionRepository txRepo,
        [FromServices] FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces.IStripeApiClient stripeApi,
        [FromServices] Microsoft.Extensions.Configuration.IConfiguration configuration,
        [FromServices] Microsoft.Extensions.Logging.ILogger<TransactionsController> logger)
    {
        Guid tenantId = HttpContext.GetTenantId();
        string? apiKey = configuration["Stripe:SecretKey"];
        if (string.IsNullOrEmpty(apiKey))
            return BadRequest(new { error = "Stripe:SecretKey não configurado." });

        var captured = await txRepo.GetCapturedSummariesAsync(tenantId);
        var orphans = new List<Guid>();
        int checkedCount = 0;
        int kept = 0;

        foreach (var tx in captured)
        {
            // Só validamos contra Stripe — OpenPix tem outro fluxo (PIX
            // captura é instantânea pelo webhook; manipulação manual é rara).
            if (tx.Provider != FellowCore.Domain.Enums.PaymentProvider.STRIPE) { kept++; continue; }

            // Sem ProviderTxId é orphan certo — TX marcada captured mas
            // nunca passou pelo Stripe.
            if (string.IsNullOrEmpty(tx.ProviderTxId))
            {
                orphans.Add(tx.Id);
                continue;
            }

            checkedCount++;
            try
            {
                var pi = await stripeApi.GetPaymentIntentAsync(apiKey, tx.ProviderTxId);
                // Sem latest_charge, sem amount_received → PI existe mas nunca
                // foi confirmado. CAPTURED local é mentira.
                if (string.IsNullOrEmpty(pi.LatestCharge) || pi.AmountReceived <= 0)
                {
                    orphans.Add(tx.Id);
                }
                else
                {
                    kept++;
                }
            }
            catch (FellowCore.Application.Exceptions.PaymentProviderException ex)
            {
                // 404 do Stripe = PI inexistente = orphan certo.
                if (ex.Message.Contains("No such payment_intent", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("resource_missing", StringComparison.OrdinalIgnoreCase))
                {
                    orphans.Add(tx.Id);
                }
                else
                {
                    // Erro inesperado — não deleta por segurança.
                    logger.LogWarning(ex, "[ORPHAN_CLEANUP] Erro consultando PI {Pi}. TX mantida.", tx.ProviderTxId);
                    kept++;
                }
            }
        }

        var deleteResult = await txRepo.DeleteByIdsAsync(orphans);
        return Ok(new
        {
            scanned = captured.Count,
            checkedAtStripe = checkedCount,
            kept,
            deletedCount = deleteResult.DeletedCount,
            deletedAmount = deleteResult.DeletedAmount
        });
    }

    [HttpPost]
    [AuditAction("transaction.created")]
    public async Task<IActionResult> CreateTransaction([FromBody] CreateTransactionDto request)
    {
        var tenantId = HttpContext.GetTenantId();

        // JWT seller-scoped → force SellerId to caller's, ignoring any value sent in body.
        var info = HttpContext.GetAuthInfo();
        if (info is { IsJwt: true, SellerId: { } scopedSellerId })
            request = request with { SellerId = scopedSellerId };

        string? idempotencyKey = HttpContext.Request.Headers["Idempotency-Key"].ToString();
        CreateTransactionDto requestWithKey = request with { IdempotencyKey = idempotencyKey };

        TransactionResponseDto? responseDto = await transactionService.CreateAsync(tenantId, requestWithKey);

        return CreatedAtAction(
                    actionName: nameof(GetTransaction),
                    routeValues: new { id = responseDto.InternalId },
                    value: responseDto
                );
    }

    [HttpGet]
    public async Task<IActionResult> ListTransactions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] TransactionStatus? status = null,
        [FromQuery] PaymentType? paymentType = null,
        [FromQuery] PaymentProvider? provider = null,
        [FromQuery] Guid? sellerId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? q = null)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();
        var filter = new TransactionFilterDto(page, pageSize, status, paymentType, provider, scopedSellerId, from, to, q);
        var result = await transactionService.ListAsync(tenantId, filter);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTransaction(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var transaction = await transactionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr404(transaction.SellerId) is { } block) return block;
        return Ok(transaction);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string format = "csv",
        [FromQuery] TransactionStatus? status = null,
        [FromQuery] PaymentType? paymentType = null,
        [FromQuery] PaymentProvider? provider = null,
        [FromQuery] Guid? sellerId = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        var (deny, scopedSellerId) = HttpContext.RequireSellerScope(sellerId);
        if (deny is not null) return deny;
        Guid tenantId = HttpContext.GetTenantId();
        var fmt = format.ToLowerInvariant();
        var fileName = $"transactions_{DateTime.UtcNow:yyyyMMdd}";

        if (fmt == "pdf")
        {
            var pdf = await exportService.ExportTransactionsPdfAsync(tenantId, from, to, scopedSellerId, status, paymentType, provider);
            return File(pdf, "application/pdf", $"{fileName}.pdf");
        }

        var csv = await exportService.ExportTransactionsCsvAsync(tenantId, from, to, scopedSellerId, status, paymentType, provider);
        return File(csv, "text/csv", $"{fileName}.csv");
    }

    [HttpPost("{id:guid}/refund")]
    [AuditAction("transaction.refunded")]
    public async Task<IActionResult> RefundTransaction(Guid id, [FromBody] RefundRequestDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await transactionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr403(existing.SellerId) is { } block) return block;

        var result = await transactionService.RefundAsync(tenantId, id, request);
        return Ok(result);
    }

    /// <summary>
    /// Calcula a quebra do reembolso sem efetivar nada. Usado pelo modal do
    /// portal pra mostrar ao seller exatamente quanto será debitado da carteira
    /// dele (líquido proporcional + taxa do provider repassada) antes da
    /// confirmação. Sem isso, o seller só vê o débito após o fato e a UX é
    /// "por que descontaram mais do que eu reembolsei?".
    /// </summary>
    [HttpPost("{id:guid}/refund/preview")]
    public async Task<IActionResult> PreviewRefund(Guid id, [FromBody] RefundRequestDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await transactionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr403(existing.SellerId) is { } block) return block;

        var breakdown = await transactionService.PreviewRefundAsync(tenantId, id, request);
        return Ok(breakdown);
    }

    [HttpGet("{id:guid}/refunds")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRefunds(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await transactionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr404(existing.SellerId) is { } block) return block;

        var refunds = await transactionService.GetRefundsAsync(tenantId, id);
        return Ok(refunds);
    }

    [HttpGet("{id:guid}/refunds/{refundId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRefundById(Guid id, string refundId)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await transactionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr404(existing.SellerId) is { } block) return block;

        var refund = await transactionService.GetRefundByIdAsync(tenantId, id, refundId);
        return Ok(refund);
    }

    [HttpGet("{id:guid}/receipt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GetReceipt(Guid id, [FromQuery] string type = "payment")
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await transactionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr404(existing.SellerId) is { } block) return block;

        var receipt = await transactionService.GetReceiptAsync(tenantId, id, type);
        return File(receipt, "application/pdf", $"receipt_{id}_{type}.pdf");
    }

    [HttpPost("{id:guid}/cancel")]
    [AuditAction("transaction.canceled")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CancelTransaction(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await transactionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr403(existing.SellerId) is { } block) return block;

        await transactionService.CancelAsync(tenantId, id);
        return NoContent();
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTransaction(Guid id, [FromBody] UpdateTransactionDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var existing = await transactionService.GetByIdAsync(tenantId, id);
        if (this.EnforceOwnershipOr403(existing.SellerId) is { } block) return block;

        var result = await transactionService.UpdateExpirationAsync(tenantId, id, request.ExpiresAt);
        return Ok(result);
    }
}

public record UpdateTransactionDto(DateTime ExpiresAt);
