using System.ComponentModel.DataAnnotations;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Marketplace.DTOs;

/// <summary>
/// Resposta pública (sem auth) usada pelo checkout. NUNCA expõe campos
/// sensíveis do produto/produtor: DeliveryUrl fica oculto (entregue só
/// após pagamento captado), TenantId/IDs internos não vão pro cliente.
/// </summary>
public record PublicProductDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    string? CoverImageUrl,
    decimal Price,
    string Currency,
    int Type,
    string? Category,
    /// <summary>Nome do produtor (TradeName ?? LegalName) — visível pra construir confiança no checkout.</summary>
    string? ProducerName,
    /// <summary>Quando ?aff=code resolve numa afiliação APPROVED, traz os dados pra UI mostrar "Indicado por X" e/ou validar.</summary>
    PublicAffiliateInfoDto? Affiliate,
    /// <summary>Facebook Pixel ID configurado pelo produtor — frontend injeta script + dispara eventos. Null = sem tracking.</summary>
    string? FacebookPixelId,
    /// <summary>Google Ads conversion id/label — frontend injeta gtag + dispara event "conversion" no success.</summary>
    string? GoogleAdsConversionId
);

public record PublicAffiliateInfoDto(
    /// <summary>Tracking code resolvido (mesmo da query string — pra confirmar match).</summary>
    string TrackingCode,
    string? AffiliateName,
    decimal CommissionPercent
);

/// <summary>
/// Payload do checkout público. Espelha PayPaymentLinkDto pra UX consistente —
/// payer info opcional, paymentType escolhido pelo cliente, parcelas se cartão.
/// </summary>
/// <summary>
/// Status mínimo retornado pelo polling público pós-checkout. Não expõe
/// PayerEmail, PayerDocument, ProviderTxId, etc — só o essencial pro front
/// detectar conclusão.
/// </summary>
public record PublicTransactionStatusDto(
    Guid Id,
    /// <summary>Nome string do TransactionStatus enum (ex: "CAPTURED", "FAILED").</summary>
    string Status,
    /// <summary>True se o status é terminal (não muda mais — CAPTURED, FAILED, VOIDED, REFUNDED).</summary>
    bool IsTerminal
);

public record PublicCheckoutRequestDto(
    [Required] PaymentType PaymentType,
    [MaxLength(120)] string? PayerName = null,
    [MaxLength(14)] string? PayerDocument = null,
    [MaxLength(120), EmailAddress] string? PayerEmail = null,
    [MaxLength(20)] string? PayerPhone = null,
    /// <summary>Tracking code do afiliado (do query param da URL ou cookie).
    /// Se válido e APPROVED, gera split automático na captura.</summary>
    [MaxLength(32)] string? TrackingCode = null,
    int? Installments = null,
    /// <summary>Código do cupom aplicado no checkout. Backend valida + aplica
    /// desconto. Cupom inválido NÃO bloqueia a compra — apenas é ignorado
    /// (mesma política do tracking code).</summary>
    [MaxLength(32)] string? CouponCode = null,
    // === UTM / Tracking de origem ===
    // Capturados na URL do checkout público pra atribuição de vendas:
    // qual campanha, canal, criativo converteu. Persistidos em Transaction.Metadata
    // e ficam disponíveis pra agregação no dashboard (afiliado e produtor).
    // Limites de 100 chars são generosos — UTMs reais raramente passam de 40,
    // mas o limite acomoda casos de campanhas com IDs longos.
    [MaxLength(100)] string? UtmSource = null,
    [MaxLength(100)] string? UtmMedium = null,
    [MaxLength(150)] string? UtmCampaign = null,
    [MaxLength(150)] string? UtmContent = null,
    [MaxLength(150)] string? UtmTerm = null,
    /// <summary>Google Ads click ID — usado por quem roda Google Ads e quer
    /// fazer offline conversion upload pra otimizar bid.</summary>
    [MaxLength(200)] string? Gclid = null,
    /// <summary>Facebook click ID — equivalente do gclid pra Meta Ads. Necessário
    /// pro Conversions API server-side mandar com dedup do pixel client-side.</summary>
    [MaxLength(200)] string? Fbclid = null,
    /// <summary>Referrer original (document.referrer) — útil quando UTMs não foram
    /// setadas mas a sessão veio de um domínio externo.</summary>
    [MaxLength(500)] string? Referrer = null,
    /// <summary>
    /// Order bumps selecionados pelo buyer no checkout. Cada GUID precisa
    /// bater num bump ativo configurado no produto principal (validado no
    /// backend). Bumps inválidos/inativos são ignorados silenciosamente — não
    /// bloqueiam a compra. Cada bump válido vira um TransactionItem adicional
    /// e adiciona o preço próprio ao total cobrado.
    /// </summary>
    List<Guid>? BumpProductIds = null
);
