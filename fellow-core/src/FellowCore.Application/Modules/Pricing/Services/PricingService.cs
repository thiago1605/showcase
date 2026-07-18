using FellowCore.Application.Common;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Pricing.Options;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FellowCore.Application.Modules.Pricing.Services;

/// <summary>
/// Sprint 1.5: pricing 100% tier-based. Não há mais PricingPlan — todas as fees
/// vêm de <see cref="TierPricingOptions.Rates"/> indexada pelo tier vigente do seller.
///
/// Resolução do tier:
///   1. Lê <see cref="SellerTierProfile"/> persistido. Se existe, usa tier dele.
///   2. Se null (seller novo / job nunca rodou): default SILVER (rates publicadas).
///   3. INFINITE com Rates[INFINITE]=null: usa <c>Seller.FeeSchedule</c> (admin
///      configura caso-a-caso — "taxa personalizada").
///
/// Métrica <see cref="IAppMetrics.RecordTierDiscountApplied"/> dispara pra qualquer
/// tier acima de SILVER (sinaliza "fee distinta da padrão") — útil pra dashboards
/// de "quanto da receita está sendo distribuído por tier".
/// </summary>
public class PricingService(
    ISellerRepository sellerRepository,
    ISellerTierProfileRepository tierProfileRepository,
    IProviderCostService providerCostService,
    IOptions<TierPricingOptions> tierPricingOptions,
    IAppMetrics metrics,
    ILogger<PricingService> logger) : IPricingService
{
    private readonly TierPricingOptions _opts = tierPricingOptions.Value;

    public Task<PlatformFeeResult> CalculatePlatformFeeAsync(
        Guid tenantId,
        Guid sellerId,
        PaymentType paymentType,
        int installments,
        decimal amount)
        => CalculatePlatformFeeAsync(tenantId, sellerId, paymentType, installments, amount, walletType: null);

    public async Task<PlatformFeeResult> CalculatePlatformFeeAsync(
        Guid tenantId,
        Guid sellerId,
        PaymentType paymentType,
        int installments,
        decimal amount,
        string? walletType)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} nao encontrado.");

        // Tier vigente (default SILVER se sem profile).
        var profile = await tierProfileRepository.GetBySellerIdAsync(tenantId, sellerId);
        var tier = profile?.Tier ?? SellerTier.SILVER;

        // INFINITE com rates null → admin configurou taxa personalizada via FeeSchedule.
        // Mesma rota antiga de FeeSchedule, mas agora é o ÚNICO caso de uso dela.
        var fees = _opts.GetFees(tier);
        if (fees is null)
        {
            logger.LogDebug(
                "Tier {Tier} sem rates configuradas — usando Seller.FeeSchedule pro seller {SellerId}",
                tier, sellerId);
            var (feeAmount, netAmount) = seller.CalculateFee(paymentType, installments, amount);
            return new PlatformFeeResult(feeAmount, netAmount);
        }

        var rate = ResolvePaymentTypeFees(fees, paymentType, installments, walletType);
        decimal rawFee = rate.Calculate(amount);
        decimal fee = RoundingPolicy.Round(rawFee);
        decimal net = RoundingPolicy.Round(amount - fee);

        logger.LogDebug(
            "Pricing seller={SellerId} tier={Tier} paymentType={PaymentType} amount={Amount} fee={Fee}",
            sellerId, tier, paymentType, amount, fee);

        // Métrica dispara pra qualquer tier acima de SILVER (sinaliza taxa distinta da padrão).
        // Silver não conta — é o "ninguém perde nem ganha" da plataforma.
        if (tier != SellerTier.SILVER)
            metrics.RecordTierDiscountApplied(tier.ToString());

        return new PlatformFeeResult(fee, net);
    }

    /// <summary>
    /// Mapeia (PaymentType + Installments + WalletType) → <see cref="PaymentTypeFees"/>.
    /// Wallet detection: CREDIT_CARD com WalletType setado (Apple Pay / Google Pay)
    /// usa a row Wallet do tier. Sem WalletType, cai em CreditCash ou CreditInstallment
    /// dependendo de Installments.
    /// </summary>
    private static PaymentTypeFees ResolvePaymentTypeFees(
        TierFees fees, PaymentType paymentType, int installments, string? walletType)
    {
        bool isWallet = paymentType == PaymentType.CREDIT_CARD && !string.IsNullOrWhiteSpace(walletType);

        return paymentType switch
        {
            PaymentType.PIX => fees.Pix,
            PaymentType.DEBIT_CARD => fees.Debit,
            PaymentType.CREDIT_CARD when isWallet => fees.Wallet,
            PaymentType.CREDIT_CARD when installments > 1 => fees.CreditInstallment,
            PaymentType.CREDIT_CARD => fees.CreditCash,
            PaymentType.BOLETO => fees.Boleto,
            _ => fees.CreditCash // defensivo — não deveria cair aqui
        };
    }

    public async Task<int> GetEffectiveMaxInstallmentsAsync(Guid tenantId, Guid sellerId)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} nao encontrado.");

        // Sem PricingPlan: default global do TierPricingOptions, override individual via Seller.
        return seller.MaxInstallments ?? _opts.DefaultMaxInstallments;
    }

    public async Task<IReadOnlyList<InstallmentOption>> GetInstallmentOptionsAsync(
        Guid tenantId,
        Guid sellerId,
        PaymentProvider provider,
        decimal amount)
    {
        var effectiveMax = await GetEffectiveMaxInstallmentsAsync(tenantId, sellerId);
        if (effectiveMax < 1) effectiveMax = 1;

        var options = new List<InstallmentOption>(effectiveMax);
        for (int n = 1; n <= effectiveMax; n++)
        {
            var feeResult = await CalculatePlatformFeeAsync(
                tenantId, sellerId, PaymentType.CREDIT_CARD, n, amount);
            var providerCost = await providerCostService.CalculateProviderCostWithInstallmentsAsync(
                provider, PaymentType.CREDIT_CARD, amount, n);
            // Modo "sem juros": comprador paga o gross. Seller absorve via provider cost maior.
            var sellerNet = RoundingPolicy.Round(amount - feeResult.PlatformFeeAmount - providerCost);
            var perInstallment = RoundingPolicy.Round(amount / n);
            options.Add(new InstallmentOption(
                Count: n,
                PerInstallmentAmount: perInstallment,
                Total: amount,
                SellerNetAmount: sellerNet,
                PlatformFeeAmount: feeResult.PlatformFeeAmount,
                ProviderCostAmount: providerCost));
        }
        return options;
    }
}
