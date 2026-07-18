using FellowCore.Api.Extensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Marketplace.Interfaces;
using FellowCore.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Endpoints do produtor pra gerenciar produtos do marketplace.
/// Sempre seller-scoped pelo JWT — produtor só edita seus próprios produtos.
/// Não-seller (operador da plataforma) recebe 403.
///
/// Routes:
///   POST   /api/v1/products                    — criar
///   GET    /api/v1/products                    — listar meus produtos
///   GET    /api/v1/products/stats               — resumo agregado (cards do painel)
///   GET    /api/v1/products/{id}               — detalhe
///   PATCH  /api/v1/products/{id}               — editar
///   POST   /api/v1/products/{id}/publish       — publicar
///   POST   /api/v1/products/{id}/pause         — pausar
///   POST   /api/v1/products/{id}/resume        — retomar
///   POST   /api/v1/products/{id}/archive       — arquivar
///   GET    /api/v1/products/{id}/affiliations  — listar afiliações do produto
/// </summary>
[ApiController]
[Route("api/v1/products")]
[Authorize]
[EnableRateLimiting("fixed")]
public class ProductsController(
    IProductService productService,
    IAffiliationService affiliationService,
    IStorageService storageService,
    ILogger<ProductsController> logger) : ControllerBase
{
    // Cover image upload — max 5MB porque é cover thumbnail, não documento
    // (Sellers já usa 10MB pra PDFs). PNG/JPEG/WEBP cobrem 99% dos casos de
    // upload de imagem em fluxo de e-commerce. SVG ficou fora — XSS surface
    // via SVG inline JS é risco e não vale a pena pra ganho marginal.
    private const long CoverMaxBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Detecta o formato real pelos magic bytes (não confia no Content-Type
    /// declarado pelo browser, que pode vir com parâmetros — `image/png;
    /// charset=binary` — ou genérico — `application/octet-stream` — ou faltar
    /// de todo). Retorna o MIME canônico ou null se não bate em nenhum
    /// formato permitido. Magic bytes lidos do início do stream.
    /// </summary>
    private static string? DetectImageMime(ReadOnlySpan<byte> header)
    {
        // PNG: 89 50 4E 47
        if (header.Length >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return "image/png";
        // JPEG (JFIF/EXIF): FF D8 FF
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return "image/jpeg";
        // WEBP: "RIFF" no offset 0 + "WEBP" no offset 8. Header de 12 bytes garante check.
        if (header.Length >= 12
            && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46   // RIFF
            && header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) // WEBP
            return "image/webp";
        return null;
    }
    private (Guid tenantId, Guid sellerId)? RequireSellerScope()
    {
        var info = HttpContext.GetAuthInfo();
        if (info is null || info.IsApiKey || info.SellerId is null) return null;
        return (info.TenantId, info.SellerId.Value);
    }

    /// <summary>
    /// Upload de capa do produto. Two-step pattern: upload primeiro, recebe URL,
    /// depois usa essa URL no `coverImageUrl` do create/update do produto. Isso
    /// evita acoplar storage I/O ao ciclo de criação (que pode falhar por
    /// vários motivos não relacionados ao upload).
    ///
    /// Multipart/form-data com field "file". Retorna { url } pra usar no form.
    /// </summary>
    [HttpPost("upload-cover")]
    [RequestSizeLimit(CoverMaxBytes + 1024)] // +1KB pra overhead do multipart envelope
    public async Task<IActionResult> UploadCover(IFormFile file)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);

        if (file is null || file.Length == 0)
            return BadRequest(new { Message = "Arquivo vazio." });
        if (file.Length > CoverMaxBytes)
            return BadRequest(new { Message = "Arquivo excede o tamanho máximo de 5 MB." });

        // Detecta o formato pelos magic bytes — não confia no Content-Type
        // declarado. Lê 12 bytes pra cobrir até WEBP (que precisa de offset 8).
        // Se o cliente mandar PNG mas o conteúdo bate com JPEG, aceitamos como
        // JPEG (formato real vence declaração). Defesa contra MIME spoofing E
        // robustez contra browsers que mandam content-type inconsistente.
        using var stream = file.OpenReadStream();
        var header = new byte[12];
        var read = await stream.ReadAsync(header.AsMemory(0, 12));
        stream.Position = 0;

        var detected = DetectImageMime(header.AsSpan(0, read));
        if (detected is null)
            return BadRequest(new { Message = "Tipo inválido. Permitidos: PNG, JPEG, WEBP." });

        // Sanitiza filename — remove path traversal + tudo que não é alfanum/hífen/_.
        // Filenames com espaços, vírgulas, parênteses (comum em downloads / screenshot
        // apps tipo "ChatGPT Image 21_05_2026, 13_41_06.png") quebram em URLs sem
        // encoding correto. MinIO armazena como literal, mas o browser/proxy pode
        // recodificar diferente em cada hop. Solução: ASCII safe set apenas.
        // O caminho do MinIO já tem GUID (`{yyyy/MM}/{guid}/{filename}`) então
        // collision é zero; o filename é só pra legibilidade humana em logs.
        var rawBase = Path.GetFileNameWithoutExtension(file.FileName ?? "cover");
        var safeBaseName = new string(rawBase
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeBaseName)) safeBaseName = "cover";
        // Trunca filename absurdamente longo — 64 chars cobre qualquer caso útil.
        if (safeBaseName.Length > 64) safeBaseName = safeBaseName[..64];
        // Extensão derivada do tipo REAL detectado (não do filename), garante
        // consistência: foo.txt com conteúdo PNG vira foo.png no storage.
        var ext = detected switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ""
        };
        var safeName = $"{safeBaseName}{ext}";

        var storageUrl = await storageService.UploadAsync(stream, safeName, detected);

        // Debug: loga headers + URLs envolvidas pra diagnosticar mismatch
        // de scheme/host quando atrás de tunnel/proxy. Remover quando o issue
        // de preview broken estiver consolidado.
        logger.LogInformation(
            "[UPLOAD_COVER] storageUrl={Storage} | scheme={Scheme} host={Host} | xfwd-proto={Proto} xfwd-host={ForwardedHost}",
            storageUrl,
            Request.Scheme,
            Request.Host.Value,
            Request.Headers["X-Forwarded-Proto"].ToString(),
            Request.Headers["X-Forwarded-Host"].ToString());

        // Reescreve a URL pra usar o proxy /api/v1/storage da própria API ao
        // invés do endpoint MinIO direto. Motivo: o MinIO roda numa rede Docker
        // interna (`http://minio:9000`) ou em hostname não acessível pelo
        // browser (quando o frontend roda atrás de tunnel / domínio externo).
        // O StorageController serve esses arquivos via stream proxy.
        //
        // Esperamos `storageUrl` no formato `{PublicUrl}/{Bucket}/{Key}` — só
        // pegamos o sufixo `{Bucket}/{Key}` e prependemos a URL ABSOLUTA da
        // API (incluindo scheme + host). Browser fetcha do mesmo domínio que
        // já está habilitado por CORS/auth.
        var rewrittenUrl = RewriteStorageUrlToProxy(storageUrl);
        logger.LogInformation("[UPLOAD_COVER] rewrittenUrl={Url}", rewrittenUrl);
        return Ok(new { url = rewrittenUrl });
    }

    /// <summary>
    /// Recebe `http://minio:9000/documents/2026/05/.../file.png` (ou similar) e
    /// devolve `https://api.example.com/api/v1/storage/documents/2026/05/.../file.png`.
    /// Estratégia: pega tudo a partir do path do bucket. Se a URL não bater no
    /// padrão esperado, devolve a original (compat — alguns ambientes podem ter
    /// MinIO realmente público).
    ///
    /// Por trás de Cloudflare Tunnel / load balancer, `Request.Scheme` reflete
    /// só o último hop (http interno) — não o protocolo original do cliente
    /// (https). Lemos `X-Forwarded-Proto` / `X-Forwarded-Host` manualmente
    /// (em vez de UseForwardedHeaders global, que muda comportamento sistêmico).
    /// Isso evita mixed-content: URL devolvida casa com o esquema da página.
    /// </summary>
    private string RewriteStorageUrlToProxy(string storageUrl)
    {
        try
        {
            var uri = new Uri(storageUrl);
            var pathOnly = uri.AbsolutePath.TrimStart('/'); // "documents/2026/05/.../file.png"
            if (string.IsNullOrWhiteSpace(pathOnly)) return storageUrl;

            // X-Forwarded-Proto pode trazer "https, http" (chain de proxies) —
            // pegamos o primeiro elemento (proto original do cliente).
            var scheme = Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim();
            if (string.IsNullOrWhiteSpace(scheme)) scheme = Request.Scheme;

            var host = Request.Headers["X-Forwarded-Host"].ToString().Split(',')[0].Trim();
            if (string.IsNullOrWhiteSpace(host)) host = Request.Host.Value ?? "";

            return $"{scheme}://{host}/api/v1/storage/{pathOnly}";
        }
        catch
        {
            return storageUrl;
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProductDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var product = await productService.CreateAsync(tenantId, sellerId, request);
        return CreatedAtAction(nameof(GetById), new { id = product.Id }, product);
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] ProductStatus? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var result = await productService.ListByOwnerAsync(tenantId, sellerId, status, page, pageSize);
        return Ok(result);
    }

    /// <summary>
    /// Resumo agregado para os cards do painel "Meus produtos":
    /// total/publicados/rascunhos/pausados (all-time) + sales/volume 30d.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats([FromQuery] int days = 30)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var stats = await productService.GetOwnerStatsAsync(tenantId, sellerId, days);
        return Ok(stats);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, _) = scope.Value;
        var product = await productService.GetByIdAsync(tenantId, id);
        if (product is null) return NotFound();
        return Ok(product);
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProductDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var product = await productService.UpdateAsync(tenantId, sellerId, id, request);
        return Ok(product);
    }

    [HttpPost("{id:guid}/publish")]
    public async Task<IActionResult> Publish(Guid id)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        return Ok(await productService.PublishAsync(tenantId, sellerId, id));
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        return Ok(await productService.PauseAsync(tenantId, sellerId, id));
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        return Ok(await productService.ResumeAsync(tenantId, sellerId, id));
    }

    [HttpPost("{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        return Ok(await productService.ArchiveAsync(tenantId, sellerId, id));
    }

    [HttpGet("{id:guid}/affiliations")]
    public async Task<IActionResult> ListAffiliations(
        Guid id,
        [FromQuery] AffiliationStatus? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var result = await affiliationService.ListByProductAsync(tenantId, sellerId, id, status, page, pageSize);
        return Ok(result);
    }

    /// <summary>
    /// Leaderboard de afiliados do produto — top performers por TPV. Só o
    /// produtor dono pode ver. Default 10 entries, máx 100.
    /// </summary>
    [HttpGet("{id:guid}/affiliates/leaderboard")]
    public async Task<IActionResult> GetLeaderboard(Guid id, [FromQuery] int limit = 10)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var entries = await affiliationService.GetLeaderboardAsync(tenantId, sellerId, id, limit);
        return Ok(entries);
    }

    // === Assets (materiais de divulgação) ===

    [HttpGet("{id:guid}/assets")]
    public async Task<IActionResult> ListAssets(Guid id)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, _) = scope.Value;
        var assets = await productService.ListAssetsAsync(tenantId, id);
        return Ok(assets);
    }

    [HttpPost("{id:guid}/assets")]
    public async Task<IActionResult> AddAsset(Guid id, [FromBody] CreateProductAssetDto request)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        var asset = await productService.AddAssetAsync(tenantId, sellerId, id, request);
        return Ok(asset);
    }

    [HttpDelete("assets/{assetId:guid}")]
    public async Task<IActionResult> DeleteAsset(Guid assetId)
    {
        var scope = RequireSellerScope();
        if (scope is null) return StatusCode(StatusCodes.Status403Forbidden);
        var (tenantId, sellerId) = scope.Value;
        await productService.DeleteAssetAsync(tenantId, sellerId, assetId);
        return NoContent();
    }
}
