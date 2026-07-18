using FellowCore.Application.Modules.Marketplace.DTOs;
using FellowCore.Application.Modules.Transactions.DTOs;

namespace FellowCore.Application.Modules.Marketplace.Interfaces;

/// <summary>
/// Checkout público do marketplace (modelo Kirvano). Endpoints anonimos —
/// resolve produto por slug + tracking code opcional, e cria a TX com
/// SplitDtos[] auto-calculado (comissão do afiliado + remainder pro produtor).
///
/// NÃO usa o seller-scope do JWT — tenant + seller (produtor) vêm do
/// Product.TenantId / Product.OwnerSellerId. Isso é seguro porque qualquer
/// um pode comprar de qualquer produto PUBLISHED.
/// </summary>
public interface IMarketplaceCheckoutService
{
    /// <summary>
    /// Resolve produto pelo slug. Aceita trackingCode opcional pra incluir
    /// dados do afiliado na resposta (UX "indicado por X"). Retorna null se
    /// o produto não existe, não está PUBLISHED, ou pertence a outro tenant
    /// que não bate com a resolução.
    ///
    /// TenantId é inferido do slug (Products.UNIQUE(TenantId, Slug)) — em
    /// teoria 2 tenants poderiam ter mesmo slug. Aqui resolvemos
    /// FIRSTBY default — multi-tenant precisa de subdomínio/host pra
    /// disambiguar. Sprint atual: single-tenant ok.
    /// </summary>
    Task<PublicProductDto?> ResolveAsync(string slug, string? trackingCode);

    /// <summary>
    /// Cria a TX pra checkout do produto. Auto-builds splits:
    ///   - Afiliado (se trackingCode válido + APPROVED): CommissionPercent% × price
    ///   - Produtor (Product.OwnerSeller): residual implícito via primary seller
    ///
    /// Co-produção via Product.SplitRuleId entra em iteração futura (combo de
    /// rule + affiliate é não-trivial).
    /// </summary>
    Task<TransactionResponseDto> CheckoutAsync(string slug, PublicCheckoutRequestDto request);

    /// <summary>
    /// Polling público de status pós-checkout — usado pelo /p/[slug] pra detectar
    /// captura após confirmação Stripe ou pagamento Pix concluído. Retorna apenas
    /// status + isTerminal, sem expor dados sensíveis.
    ///
    /// Tenant é inferido do slug (Product.TenantId), igual ao Resolve/Checkout —
    /// evita expor um lookup global por transactionId. Slug + txId precisam bater:
    /// se a TX não pertence ao produto do slug, retorna null.
    /// </summary>
    Task<PublicTransactionStatusDto?> GetStatusAsync(string slug, Guid transactionId);

    /// <summary>
    /// Registra um clique no link de divulgação de afiliado. Anônimo, deve ser
    /// chamado pelo /p/[slug] quando carrega com ?aff={code} válido e ativo.
    /// Dedup interno (mesma fingerprint+afiliação em &lt;1h conta como 1 click).
    /// IP + UA são hasheados pra fingerprint antes de chegar no domain —
    /// nunca persiste PII crua.
    /// </summary>
    Task RecordAffiliateClickAsync(
        string trackingCode,
        string ipAddress,
        string userAgent,
        string? referrer);
}
