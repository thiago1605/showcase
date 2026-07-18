using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FellowCore.Api.Controllers;

/// <summary>
/// Proxy público de leitura do storage (MinIO). Resolve o problema clássico
/// de servir uploads em ambientes onde o storage interno (`http://minio:9000`)
/// não é acessível pelo browser do cliente — só a API é exposta via Cloudflare
/// Tunnel / load balancer.
///
/// Fluxo:
///   Browser → GET https://api.example.com/api/v1/storage/documents/path/to/file.png
///   API     → S3 GetObject "documents/path/to/file.png" (rede interna Docker)
///   API     → stream do response body de volta pro browser
///
/// Cacheable por design — files no MinIO são content-addressable
/// (`{yyyy/MM}/{guid}/{filename}`) então nunca mudam após upload. Header
/// `Cache-Control: public, max-age=31536000, immutable` permite cache no
/// browser + CDN sem invalidar.
///
/// Anonymous porque imagens de produto são públicas por design (aparecem no
/// checkout). Files privados (futuros) precisariam de outro endpoint com auth
/// + check de ownership.
/// </summary>
[ApiController]
[Route("api/v1/storage")]
[AllowAnonymous]
[EnableRateLimiting("fixed")]
public class StorageController(IAmazonS3 s3Client) : ControllerBase
{
    /// <summary>Buckets que podem ser servidos publicamente. Allowlist defensiva —
    /// evita que alguém descubra outros buckets internos do MinIO via path enumeration.</summary>
    private static readonly HashSet<string> PublicBuckets = new(StringComparer.OrdinalIgnoreCase)
    {
        "documents", // bucket default usado pelo MinioStorageService
    };

    /// <summary>
    /// Catch-all do path interno do bucket. `{**path}` no route template permite
    /// que o path inclua "/" (necessário pra `2026/05/{guid}/file.png`).
    /// </summary>
    [HttpGet("{bucket}/{**path}")]
    public async Task<IActionResult> GetObject(string bucket, string path)
    {
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(path))
            return NotFound();
        if (!PublicBuckets.Contains(bucket))
            return NotFound();

        // Defesa anti path traversal — `..` é normalizado pelo S3 mas validamos
        // proativamente pra log/observabilidade.
        if (path.Contains("..") || path.Contains("//"))
            return NotFound();

        try
        {
            var response = await s3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = path
            });

            // Cache-Control aggressive — conteúdo é immutable (path content-addressable).
            Response.Headers.CacheControl = "public, max-age=31536000, immutable";

            return File(response.ResponseStream, response.Headers.ContentType ?? "application/octet-stream");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound();
        }
    }
}
