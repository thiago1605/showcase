using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Pricing.Options;

/// <summary>
/// Sprint 1.5: tabela ÚNICA de pricing do FellowPay. Substitui o sistema de
/// PricingPlan (Fase 1) por estrutura tier-only — cada tier define seu próprio
/// conjunto de fees por payment type. Não há mais "planos comerciais"; o pricing
/// é função pura do tier vigente do seller (que por sua vez é função do TPV).
///
/// Marketing alinhado:
///   "Comece com 2,9% no PIX. Cresceu, paga menos. Sem mensalidade, sem fidelidade."
///
/// INFINITE: <see cref="Rates"/>[INFINITE] pode ser null. Quando null, sellers
/// nesse tier usam <c>Seller.FeeSchedule</c> (admin configura caso-a-caso —
/// "Taxa personalizada / convite exclusivo"). Permitir Infinite na tabela
/// pública também funciona se quiser publicar um teto.
///
/// Floor de margem (<see cref="FloorMarginMultiplier"/>): garante que cada
/// combinação tier × paymentType deixa margem ≥ X% do provider cost. Validado
/// no boot por <c>TierPricingFloorValidator</c>.
/// </summary>
public sealed class TierPricingOptions
{
    public const string SectionName = "TierPricing";

    /// <summary>Tabela completa de fees por tier. INFINITE pode ser null (usa Seller.FeeSchedule).</summary>
    public Dictionary<SellerTier, TierFees?> Rates { get; set; } = DefaultRates();

    /// <summary>
    /// Floor: margem ≥ provider_cost × este multiplier. Default 15% — realista pra
    /// crédito onde Stripe cost (3,99% + R$ 0,39) come quase toda a fee. PIX folga
    /// com sobra.
    /// </summary>
    public decimal FloorMarginMultiplier { get; set; } = 0.15m;

    /// <summary>
    /// Payment types pulados na validação de floor. Defaults:
    ///   - BOLETO: custo fixo Stripe R$ 3,45 vs fee R$ 3,49 = margem ~zero
    ///     ("loss leader" de conclusão, não diferencial competitivo)
    ///   - DEBIT_CARD: custo Stripe BR débito (3,99%) é IGUAL ao crédito; nossa fee
    ///     de débito é tradicionalmente menor que crédito → margem estruturalmente
    ///     fina. Reavaliar quando trocarmos a tabela ProviderCostSchedule pra refletir
    ///     custo real de débito (1,99% no Brasil) — aí dá pra incluir no floor.
    /// PIX e CREDIT_CARD sempre validados.
    /// </summary>
    public HashSet<PaymentType> SkipFloorValidation { get; set; } = new()
    {
        PaymentType.BOLETO,
        PaymentType.DEBIT_CARD
    };

    /// <summary>Hard-fail no boot se config viola floor. Em dev pode desligar.</summary>
    public bool EnforceFloorAtStartup { get; set; } = true;

    /// <summary>
    /// Default global de parcelas máximas em crédito. Sellers podem override
    /// individual via <c>Seller.MaxInstallments</c>. Pré-tier era no plano.
    /// </summary>
    public int DefaultMaxInstallments { get; set; } = 12;

    /// <summary>
    /// Taxa de antecipação (%) sobre o valor adiantado quando o seller opta por
    /// modo ADVANCE (recebe D+30 em parcela única em vez de mensal). Pré-Sprint 1.5
    /// era setado por plano (<c>PricingPlan.AdvancePercentFee</c>); agora é global,
    /// configurável via <c>TierPricing:AdvancePercentFee</c>. Sprint 2: per-tier
    /// (BLACK adianta com taxa menor que SILVER, alinhando com a economia de TPV).
    /// Default 5% — está no meio do range de antecipação de mercado (3-8%).
    /// </summary>
    public decimal AdvancePercentFee { get; set; } = 5.00m;

    /// <summary>
    /// Resolve as fees pra um tier. Pra INFINITE com Rates=null, retorna null —
    /// caller deve cair pro <c>Seller.FeeSchedule</c>.
    /// </summary>
    public TierFees? GetFees(SellerTier tier)
        => Rates.TryGetValue(tier, out var f) ? f : null;

    /// <summary>
    /// Tabela default seedada com os valores aprovados pelo time comercial
    /// (Sprint 1.5, baseada no marketing doc 2026-05-19).
    /// PIX é o headline: bate exatamente com a tabela publicada.
    /// Outras formas seguem proporção pra cumprir o floor de margem 30%.
    /// </summary>
    public static Dictionary<SellerTier, TierFees?> DefaultRates() => new()
    {
        // Boleto fixo em R$ 3,49 pra todos tiers — custo Stripe (R$ 3,45) deixa margem
        // ~zero. Não há gordura pra discount por tier no boleto. Cards têm spread fino
        // mas viável; PIX é onde a marketing pitch realmente brilha.
        [SellerTier.SILVER] = new TierFees
        {
            Pix = new PaymentTypeFees { Percent = 2.90m, Fixed = 0.47m, Min = 0.50m },
            CreditCash = new PaymentTypeFees { Percent = 4.99m, Fixed = 0.49m },
            CreditInstallment = new PaymentTypeFees { Percent = 5.99m, Fixed = 0.49m },
            Debit = new PaymentTypeFees { Percent = 3.99m, Fixed = 0.49m },
            Boleto = new PaymentTypeFees { Percent = 0m, Fixed = 3.49m },
            Wallet = new PaymentTypeFees { Percent = 4.99m, Fixed = 0.49m },
        },
        [SellerTier.GOLD] = new TierFees
        {
            Pix = new PaymentTypeFees { Percent = 2.70m, Fixed = 0.39m, Min = 0.40m },
            CreditCash = new PaymentTypeFees { Percent = 4.89m, Fixed = 0.49m },
            CreditInstallment = new PaymentTypeFees { Percent = 5.89m, Fixed = 0.49m },
            Debit = new PaymentTypeFees { Percent = 3.89m, Fixed = 0.49m },
            Boleto = new PaymentTypeFees { Percent = 0m, Fixed = 3.49m },
            Wallet = new PaymentTypeFees { Percent = 4.89m, Fixed = 0.49m },
        },
        [SellerTier.DIAMOND] = new TierFees
        {
            Pix = new PaymentTypeFees { Percent = 2.50m, Fixed = 0.29m, Min = 0.30m },
            CreditCash = new PaymentTypeFees { Percent = 4.79m, Fixed = 0.49m },
            CreditInstallment = new PaymentTypeFees { Percent = 5.79m, Fixed = 0.49m },
            Debit = new PaymentTypeFees { Percent = 3.79m, Fixed = 0.49m },
            Boleto = new PaymentTypeFees { Percent = 0m, Fixed = 3.49m },
            Wallet = new PaymentTypeFees { Percent = 4.79m, Fixed = 0.49m },
        },
        [SellerTier.BLACK] = new TierFees
        {
            Pix = new PaymentTypeFees { Percent = 2.40m, Fixed = 0.19m, Min = 0.20m },
            CreditCash = new PaymentTypeFees { Percent = 4.69m, Fixed = 0.49m },
            CreditInstallment = new PaymentTypeFees { Percent = 5.69m, Fixed = 0.49m },
            Debit = new PaymentTypeFees { Percent = 3.69m, Fixed = 0.49m },
            Boleto = new PaymentTypeFees { Percent = 0m, Fixed = 3.49m },
            Wallet = new PaymentTypeFees { Percent = 4.69m, Fixed = 0.49m },
        },
        // INFINITE: convite exclusivo, taxa personalizada por admin via
        // Seller.FeeSchedule. Quando null aqui, PricingService cai pro FeeSchedule.
        [SellerTier.INFINITE] = null,
    };
}

/// <summary>Conjunto de fees pro seller em um tier — uma entrada por payment type.</summary>
public sealed class TierFees
{
    public PaymentTypeFees Pix { get; set; } = new();
    public PaymentTypeFees CreditCash { get; set; } = new();
    public PaymentTypeFees CreditInstallment { get; set; } = new();
    public PaymentTypeFees Debit { get; set; } = new();
    public PaymentTypeFees Boleto { get; set; } = new();
    public PaymentTypeFees Wallet { get; set; } = new();
}

/// <summary>
/// Fórmula da fee: <c>max(Min ?? 0, min(Max ?? +inf, Percent% × amount + Fixed))</c>.
/// Em payment types sem percentual (boleto fixo, ex), Percent = 0.
/// </summary>
public sealed class PaymentTypeFees
{
    /// <summary>Componente % do fee (0 = só fixed, ex: boleto).</summary>
    public decimal Percent { get; set; }

    /// <summary>Componente fixo do fee em R$ (0 = só percentual).</summary>
    public decimal Fixed { get; set; }

    /// <summary>Fee mínima (R$) — protege contra TX muito pequenas. Null = sem mínimo.</summary>
    public decimal? Min { get; set; }

    /// <summary>Fee máxima (R$) — caps fee em transações grandes. Null = sem teto.</summary>
    public decimal? Max { get; set; }

    /// <summary>Calcula a fee final pra um amount, aplicando Min/Max se setados.</summary>
    public decimal Calculate(decimal amount)
    {
        decimal raw = amount * Percent / 100m + Fixed;
        if (Max.HasValue) raw = Math.Min(raw, Max.Value);
        if (Min.HasValue) raw = Math.Max(raw, Min.Value);
        return raw;
    }
}
