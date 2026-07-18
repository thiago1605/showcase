using FellowCore.Api.Auth;
using FellowCore.Api.Extensions;
using FellowCore.Api.Filters;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Sellers.DTOs;
using FellowCore.Application.Modules.Sellers.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

[ApiController]
[Route("api/v1/sellers")]
[ApiKeyAuth]
[EnableRateLimiting("fixed")]
public class SellersController(ISellerService sellerService, IStorageService storageService) : ControllerBase
{
    [HttpPost]
    [AuditAction("seller.created")]
    public async Task<IActionResult> CreateSeller([FromBody] CreateSellerDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        SellerResponseDto? responseDto = await sellerService.CreateAsync(tenantId, request);
        
        return CreatedAtAction(
            actionName: nameof(GetSeller),
            routeValues: new { id = responseDto.Id },
            value: responseDto
        );
    }

    [HttpGet]
    public async Task<IActionResult> GetSellers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.ListAsync(tenantId, page, pageSize);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetSeller(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var seller = await sellerService.GetByIdAsync(tenantId, id);
        return Ok(seller);
    }

    [HttpPatch("{id:guid}")]
    [AuditAction("seller.updated")]
    public async Task<IActionResult> UpdateSeller(Guid id, [FromBody] UpdateSellerDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.UpdateAsync(tenantId, id, request);
        return Ok(result);
    }

    /// <summary>
    /// Marca/desmarca o seller como Founding Seller. Settable apenas por admin
    /// (via ApiKey de tenant) — não é derivado de TPV. Útil pro lançamento:
    /// transforma os primeiros sellers em "Founding #1", "Founding #2", etc,
    /// e expõe o número no portal pra criar narrativa de exclusividade.
    /// FoundingNumber deve ser único por tenant — DB rejeita duplicidade.
    /// </summary>
    [HttpPut("{id:guid}/founding")]
    [AuditAction("seller.founding.set")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetFounding(Guid id, [FromBody] SetFoundingSellerDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.SetFoundingAsync(tenantId, id, request);
        return Ok(result);
    }

    [HttpGet("{id:guid}/statement")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSellerStatement(
        Guid id,
        [FromQuery] DateTime? start = null,
        [FromQuery] DateTime? end = null)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var statement = await sellerService.GetStatementAsync(tenantId, id, start, end);
        return Ok(statement);
    }

    [HttpGet("{id:guid}/balance")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSellerBalance(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var balance = await sellerService.GetBalanceAsync(tenantId, id);
        return Ok(balance);
    }

    /// <summary>
    /// Provisiona retroativamente a Connected Account do provider pra um seller
    /// criado sem ela (tipicamente sellers seedados direto no banco). Sem essa
    /// conta, transações desse seller deixam o dinheiro na plataforma —
    /// armadilha contábil. O fail-fast no <c>StripePaymentProvider</c> agora
    /// bloqueia esse cenário; esse endpoint é a saída.
    /// </summary>
    [HttpPost("{id:guid}/stripe-connect/provision")]
    [AuditAction("seller.stripe_connect.provisioned")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ProvisionStripeConnect(Guid id, [FromBody] ProvisionConnectAccountDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.ProvisionConnectAccountAsync(tenantId, id, request);
        return Ok(result);
    }

    /// <summary>
    /// Compara o saldo do seller no ledger interno com o saldo real na conta
    /// Stripe Connect dele. READ-ONLY — só relata. Útil pra detectar dinheiro
    /// fantasma (ledger crédito mas caixa Stripe vazio) ou descompasso oposto
    /// (Stripe creditou mas ledger não viu).
    /// </summary>
    [HttpPost("{id:guid}/stripe-sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SyncStripeBalance(Guid id)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.SyncStripeBalanceAsync(tenantId, id);
        return Ok(result);
    }

    /// <summary>
    /// DESTRUTIVO: ajusta o saldo do seller no ledger pra match com a conta
    /// Stripe Connect dele. Cria entries de WALLET e FUTURE_RECEIVABLES com
    /// ReferenceType=BALANCE_RECONCILE pra audit. Exige `reason` no body.
    /// </summary>
    [HttpPost("{id:guid}/stripe-reconcile")]
    [AuditAction("seller.stripe_reconciled")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReconcileWithStripe(Guid id, [FromBody] StripeReconcileRequestDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.ReconcileWithStripeAsync(tenantId, id, request.Reason);
        return Ok(result);
    }

    [HttpPost("{id:guid}/withdraw")]
    [AuditAction("seller.withdraw")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Withdraw(Guid id, [FromBody] SellerWithdrawRequestDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.WithdrawAsync(tenantId, id, request);
        return Ok(result);
    }

    /// <summary>
    /// Saque multi-provider via saga orchestrator. Distribui o amount entre
    /// WALLETs Stripe/OpenPix do seller (ordenado pelo PreferredProvider),
    /// chama cada provider sequencialmente com idempotência, e compensa
    /// automaticamente se algum step falhar. Retorna o WithdrawalAttempt
    /// com status COMPLETED/PARTIALLY_COMPLETED/FAILED.
    /// </summary>
    [HttpPost("{id:guid}/withdraw-multi")]
    [AuditAction("seller.withdraw_multi")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> WithdrawMulti(
        Guid id,
        [FromBody] SellerWithdrawRequestDto request,
        [FromServices] FellowCore.Application.Modules.Payouts.Services.IWithdrawOrchestrator orchestrator)
    {
        Guid tenantId = HttpContext.GetTenantId();
        string? idempotencyKey = HttpContext.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrEmpty(idempotencyKey)) idempotencyKey = null;

        var attempt = await orchestrator.ExecuteAsync(tenantId, id, request.Amount, idempotencyKey, HttpContext.RequestAborted);
        return Ok(MapAttempt(attempt));
    }

    /// <summary>Consulta status de um saque multi-provider.</summary>
    [HttpGet("{id:guid}/withdrawals/{attemptId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWithdrawal(
        Guid id, Guid attemptId,
        [FromServices] FellowCore.Domain.Interfaces.IWithdrawalAttemptRepository attemptRepo)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var attempt = await attemptRepo.GetByIdAsync(tenantId, attemptId);
        if (attempt == null || attempt.SellerId != id) return NotFound();
        return Ok(MapAttempt(attempt));
    }

    private static object MapAttempt(FellowCore.Domain.Entities.WithdrawalAttempt a) => new
    {
        attemptId = a.Id,
        sellerId = a.SellerId,
        requestedAmount = a.RequestedAmount,
        status = a.Status.ToString(),
        failureSummary = a.FailureSummary,
        createdAt = a.CreatedAt,
        updatedAt = a.UpdatedAt,
        steps = a.Steps
            .OrderBy(s => s.Sequence)
            .Select(s => new
            {
                stepId = s.Id,
                provider = s.Provider.ToString(),
                amount = s.Amount,
                sequence = s.Sequence,
                status = s.Status.ToString(),
                providerPayoutId = s.ProviderPayoutId,
                lastError = s.LastError,
                attemptCount = s.AttemptCount,
            })
            .ToList()
    };

    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private static readonly string[] AllowedMimeTypes = ["application/pdf", "image/png", "image/jpeg"];
    private static readonly string[] AllowedDocumentTypes = ["SOCIAL_CONTRACT", "ATA", "BYLAWS"];

    private static readonly Dictionary<string, byte[][]> MagicBytes = new()
    {
        ["application/pdf"] = [new byte[] { 0x25, 0x50, 0x44, 0x46 }], // %PDF
        ["image/png"] = [new byte[] { 0x89, 0x50, 0x4E, 0x47 }],      // .PNG
        ["image/jpeg"] = [new byte[] { 0xFF, 0xD8, 0xFF }]             // JFIF/EXIF
    };

    [HttpPost("documents/upload")]
    public async Task<IActionResult> UploadDocument(IFormFile file, [FromForm] string type)
    {
        if (file.Length == 0)
            return BadRequest(new { error = "Arquivo vazio." });

        if (file.Length > MaxFileSizeBytes)
            return BadRequest(new { error = "Arquivo excede o tamanho maximo de 10 MB." });

        if (!AllowedMimeTypes.Contains(file.ContentType))
            return BadRequest(new { error = "Tipo de arquivo invalido. Permitidos: PDF, PNG, JPEG." });

        if (!AllowedDocumentTypes.Contains(type))
            return BadRequest(new { error = "Tipo de documento invalido. Use: SOCIAL_CONTRACT, ATA ou BYLAWS." });

        // Validate magic bytes to prevent MIME spoofing
        using var stream = file.OpenReadStream();
        var header = new byte[4];
        int bytesRead = await stream.ReadAsync(header.AsMemory(0, 4));
        stream.Position = 0;

        if (bytesRead < 3 || !ValidateMagicBytes(file.ContentType, header))
            return BadRequest(new { error = "Conteudo do arquivo nao corresponde ao tipo declarado." });

        // Sanitize filename
        var safeFileName = Path.GetFileNameWithoutExtension(file.FileName)
            .Replace("..", "").Replace("/", "").Replace("\\", "");
        var extension = Path.GetExtension(file.FileName);
        var sanitizedName = $"{safeFileName}{extension}";

        string url = await storageService.UploadAsync(stream, sanitizedName, file.ContentType);

        return Ok(new { url, type });
    }

    private static bool ValidateMagicBytes(string mimeType, byte[] header)
    {
        if (!MagicBytes.TryGetValue(mimeType, out var signatures)) return false;
        return signatures.Any(sig => header.AsSpan(0, sig.Length).SequenceEqual(sig));
    }

    // --- Subaccount endpoints ---

    [HttpPost("subaccounts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateSubAccount([FromBody] CreateSubAccountDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.CreateSubAccountAsync(tenantId, request);
        return Ok(result);
    }

    [HttpGet("subaccounts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSubAccounts()
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.ListSubAccountsAsync(tenantId);
        return Ok(result);
    }

    [HttpGet("subaccounts/{pixKeyOrId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSubAccount(string pixKeyOrId)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.GetSubAccountAsync(tenantId, pixKeyOrId);
        return Ok(result);
    }

    [HttpDelete("subaccounts/{pixKeyOrId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSubAccount(string pixKeyOrId)
    {
        Guid tenantId = HttpContext.GetTenantId();
        await sellerService.DeleteSubAccountAsync(tenantId, pixKeyOrId);
        return NoContent();
    }

    [HttpPost("subaccounts/{pixKeyOrId}/credit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> CreditSubAccount(string pixKeyOrId, [FromBody] SubAccountCreditDebitDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.CreditSubAccountAsync(tenantId, pixKeyOrId, request);
        return Ok(result);
    }

    [HttpPost("subaccounts/{pixKeyOrId}/debit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DebitSubAccount(string pixKeyOrId, [FromBody] SubAccountCreditDebitDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.DebitSubAccountAsync(tenantId, pixKeyOrId, request);
        return Ok(result);
    }

    [HttpPost("subaccounts/transfer")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TransferBetweenSubAccounts([FromBody] SubAccountTransferDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.TransferBetweenSubAccountsAsync(tenantId, request);
        return Ok(result);
    }

    [HttpPost("subaccounts/{pixKeyOrId}/withdraw")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> WithdrawFromSubAccount(string pixKeyOrId, [FromBody] SubAccountWithdrawDto request)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.WithdrawFromSubAccountAsync(tenantId, pixKeyOrId, request);
        return Ok(result);
    }

    [HttpGet("subaccounts/{pixKeyOrId}/statement")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSubAccountStatement(
        string pixKeyOrId,
        [FromQuery] DateTime? start = null,
        [FromQuery] DateTime? end = null)
    {
        Guid tenantId = HttpContext.GetTenantId();
        var result = await sellerService.GetSubAccountStatementAsync(tenantId, pixKeyOrId, start, end);
        return Ok(result);
    }
}