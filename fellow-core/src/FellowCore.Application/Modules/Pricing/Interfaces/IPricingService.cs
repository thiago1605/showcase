using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Pricing.Interfaces;

public interface IPricingService
{
    Task<PlatformFeeResult> CalculatePlatformFeeAsync(
        Guid tenantId,
        Guid sellerId,
        PaymentType paymentType,
        int installments,
        decimal amount);

    /// <summary>
    /// Versão async com suporte a <paramref name="walletType"/>. Pra TX
    /// CREDIT_CARD com WalletType ("apple_pay" / "google_pay"), aplica a
    /// fee row de wallet do tier. Quando não setado, comportamento idêntico
    /// à overload sem wallet.
    /// </summary>
    Task<PlatformFeeResult> CalculatePlatformFeeAsync(
        Guid tenantId,
        Guid sellerId,
        PaymentType paymentType,
        int installments,
        decimal amount,
        string? walletType);

    /// <summary>
    /// Limite efetivo de parcelas pro seller:
    ///   1. <c>Seller.MaxInstallments</c> (override caso-a-caso)
    ///   2. <c>TierPricingOptions.DefaultMaxInstallments</c> (default global, ex. 12)
    /// </summary>
    Task<int> GetEffectiveMaxInstallmentsAsync(Guid tenantId, Guid sellerId);

    /// <summary>
    /// Breakdown completo das opções de parcelamento disponíveis pra um
    /// pagamento de cartão. Inclui platform fee + provider cost + surcharge
    /// adicional por parcela, e o que o seller recebe líquido em cada N.
    /// Útil pra UI checkout exibir as opções com preview de impacto.
    /// </summary>
    Task<IReadOnlyList<InstallmentOption>> GetInstallmentOptionsAsync(
        Guid tenantId,
        Guid sellerId,
        PaymentProvider provider,
        decimal amount);
}

public record PlatformFeeResult(decimal PlatformFeeAmount, decimal SellerNetAmount);

/// <summary>
/// Uma opção de parcelamento exibível no checkout. Modo "sem juros":
/// <c>Total</c> = <c>Amount</c> (comprador paga o mesmo, seller absorve fee).
/// </summary>
public record InstallmentOption(
    int Count,
    decimal PerInstallmentAmount,
    decimal Total,
    decimal SellerNetAmount,
    decimal PlatformFeeAmount,
    decimal ProviderCostAmount);
