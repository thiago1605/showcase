using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Common.Utils;
using FellowCore.Application.Modules.Notifications.DTOs;
using FellowCore.Application.Modules.Notifications.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

/// <summary>
/// Processor pra entrega de webhooks pros endpoints configurados. Dispara para
/// dois grupos de uma vez:
///   1. Tenant-wide (SellerId IS NULL) — devs/integradores que assinaram TODOS
///      os eventos do tenant.
///   2. Producer-scoped (SellerId == TX.SellerId) — producer recebe só os
///      eventos das próprias vendas. Usado em integrações de marketing
///      automation (RD Station, ActiveCampaign, Mailchimp).
///
/// Os 2 grupos recebem o MESMO payload enriquecido (customer, product, affiliate,
/// utm). Originalmente o tenant-wide tinha payload mínimo; unificamos pra evitar
/// 2 caminhos de serialização e porque os dados extras são úteis pros 2 públicos.
/// </summary>
public class TenantWebhookProcessor(
    IWebhookEndpointRepository webhookEndpointRepository,
    ITransactionRepository transactionRepository,
    ICustomerRepository customerRepository,
    IProductRepository productRepository,
    ISellerRepository sellerRepository,
    ISecurityService securityService,
    IHttpClientFactory httpClientFactory,
    IAppMetrics metrics,
    ILogger<TenantWebhookProcessor> logger) : ITenantWebhookProcessor
{
    public async Task ProcessAsync(NotificationJobData job)
    {
        // Carrega TODOS os endpoints aplicáveis: tenant-wide (SellerId IS NULL)
        // UNION producer-scoped (SellerId == job.SellerId). Antes só pegava
        // tenant-wide — agora o webhook do producer também é entregue aqui.
        var endpoints = await webhookEndpointRepository.GetActiveForSellerEventAsync(job.TenantId, job.SellerId);

        if (endpoints.Count == 0)
        {
            logger.LogDebug(
                "Nenhum webhook endpoint ativo para Tenant {TenantId} / Seller {SellerId}",
                job.TenantId, job.SellerId);
            return;
        }

        string eventType = $"transaction.{job.Status.ToString().ToLowerInvariant()}";

        // Enriquece o payload com customer/product/affiliate/utm uma única vez.
        // O DTO completo é compartilhado por todos os endpoints — mesmo o tenant-wide
        // recebe os campos extras (que são opcionais, não quebra integrações antigas).
        var enrichedData = await BuildEnrichedPayloadAsync(job);

        var payload = new SellerWebhookPayload(
            Event: eventType,
            Data: enrichedData
        );

        string jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (var endpoint in endpoints)
        {
            if (endpoint.Events.Count > 0 && !endpoint.Events.Contains(eventType))
                continue;

            await DeliverAsync(endpoint, payload, jsonPayload, eventType, job);
        }

        await webhookEndpointRepository.SaveChangesAsync();
    }

    private async Task DeliverAsync(
        WebhookEndpoint endpoint,
        SellerWebhookPayload payload,
        string jsonPayload,
        string eventType,
        NotificationJobData job)
    {
        var sw = Stopwatch.StartNew();
        int? responseCode = null;
        bool success = false;
        string? error = null;

        try
        {
            using var client = httpClientFactory.CreateClient("WebhookClient");
            client.Timeout = TimeSpan.FromSeconds(5);

            string decryptedSecret = await securityService.DecryptAsync(endpoint.Secret);
            string signature = CryptoUtils.GenerateHmacSha256(jsonPayload, decryptedSecret);
            client.DefaultRequestHeaders.Add("X-Webhook-Signature", signature);
            client.DefaultRequestHeaders.Add("X-Webhook-Event", eventType);
            // Producer-scoped endpoints podem usar isso pra distinguir entre
            // webhooks da plataforma (todos os sellers) vs próprios.
            client.DefaultRequestHeaders.Add("X-Webhook-Scope", endpoint.SellerId.HasValue ? "seller" : "tenant");
            client.DefaultRequestHeaders.Add("User-Agent", "FellowCoreGateway/1.0");

            var response = await client.PostAsJsonAsync(endpoint.Url, payload);
            responseCode = (int)response.StatusCode;
            success = response.IsSuccessStatusCode;

            if (!success)
            {
                error = $"HTTP {responseCode}";
                metrics.RecordWebhookDelivery("failed");
                metrics.RecordWebhookDeliveryFailure();
            }
            else
            {
                metrics.RecordWebhookDelivery("success");
                logger.LogInformation(
                    "Webhook entregue. Url={Url} Status={Code} Scope={Scope}",
                    endpoint.Url, responseCode, endpoint.SellerId.HasValue ? "seller" : "tenant");
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            metrics.RecordWebhookDelivery("failed");
            metrics.RecordWebhookDeliveryFailure();
            logger.LogError(ex, "Falha ao entregar webhook para {Url}", endpoint.Url);
        }
        finally
        {
            sw.Stop();
            endpoint.RecordDelivery(
                eventId: job.TransactionId.ToString(),
                eventType: eventType,
                payload: JsonSerializer.SerializeToDocument(payload),
                responseCode: responseCode,
                success: success,
                duration: (int)sw.ElapsedMilliseconds,
                error: error);
        }
    }

    /// <summary>
    /// Constrói o payload enriquecido com customer/product/affiliate/utm.
    /// Best-effort: qualquer falha de lookup retorna null no campo correspondente
    /// (não quebra a entrega do webhook). Acessamos via Tx → relacionados
    /// (customer por CustomerId, produto via ExternalReferenceId="product:{id}",
    /// UTM/affiliate via Tx.Metadata).
    /// </summary>
    private async Task<SellerWebhookData> BuildEnrichedPayloadAsync(NotificationJobData job)
    {
        WebhookCustomerData? customer = null;
        WebhookProductData? product = null;
        WebhookAffiliateData? affiliate = null;
        WebhookUtmData? utm = null;
        string? externalRef = null;

        try
        {
            var tx = await transactionRepository.GetByIdAsync(job.TenantId, job.TransactionId);
            if (tx != null)
            {
                externalRef = tx.ExternalReferenceId;

                // Customer: prefere PayerEmail/PayerName do checkout público,
                // fallback pro Customer entity ligado pelo CustomerId.
                string? email = tx.PayerEmail;
                string? name = tx.PayerName;
                string? document = tx.PayerDocument;
                if (string.IsNullOrEmpty(email) && tx.CustomerId.HasValue)
                {
                    var c = await customerRepository.GetByIdAsync(job.TenantId, tx.CustomerId.Value);
                    if (c != null)
                    {
                        email ??= c.Email;
                        name ??= c.Name;
                        document ??= c.Document;
                    }
                }
                if (!string.IsNullOrEmpty(email) || !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(document))
                    customer = new WebhookCustomerData(email, name, document);

                // Product: TXs do marketplace seguem padrão "product:{guid}".
                const string prefix = "product:";
                if (!string.IsNullOrEmpty(tx.ExternalReferenceId)
                    && tx.ExternalReferenceId.StartsWith(prefix, StringComparison.Ordinal)
                    && Guid.TryParse(tx.ExternalReferenceId.AsSpan(prefix.Length), out var productId))
                {
                    var p = await productRepository.GetByIdAsync(job.TenantId, productId);
                    if (p != null)
                        product = new WebhookProductData(p.Id, p.Name, p.Slug);
                }

                // UTM + Affiliate: extraídos do Metadata JSON da TX se presentes.
                // Convenção: keys "utm_source", "utm_medium", etc; "affiliate_id"
                // como GUID e "affiliate_name" como string.
                if (tx.Metadata != null)
                {
                    var root = tx.Metadata.RootElement;
                    string? source = TryGetString(root, "utm_source");
                    string? medium = TryGetString(root, "utm_medium");
                    string? campaign = TryGetString(root, "utm_campaign");
                    string? content = TryGetString(root, "utm_content");
                    string? term = TryGetString(root, "utm_term");
                    if (source != null || medium != null || campaign != null || content != null || term != null)
                        utm = new WebhookUtmData(source, medium, campaign, content, term);

                    string? affIdStr = TryGetString(root, "affiliate_id");
                    if (affIdStr != null && Guid.TryParse(affIdStr, out var affId))
                    {
                        string? affName = TryGetString(root, "affiliate_name");
                        if (affName == null)
                        {
                            // Resolve nome via Seller (affiliate é seller no DB)
                            var affSeller = await sellerRepository.GetByIdAsync(job.TenantId, affId);
                            affName = affSeller?.TradeName ?? affSeller?.LegalName;
                        }
                        affiliate = new WebhookAffiliateData(affId, affName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Enrichment é best-effort — webhook ainda dispara com payload mínimo.
            logger.LogWarning(ex,
                "Falha ao enriquecer payload do webhook para TX {TxId}. Enviando com dados mínimos.",
                job.TransactionId);
        }

        return new SellerWebhookData(
            Id: job.TransactionId,
            ProviderId: job.ProviderTxId,
            Status: job.Status.ToString(),
            Amount: job.NetAmount,
            Type: job.PaymentType.ToString(),
            UpdatedAt: DateTime.UtcNow,
            Customer: customer,
            Product: product,
            Affiliate: affiliate,
            Utm: utm,
            ExternalReferenceId: externalRef);
    }

    private static string? TryGetString(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
