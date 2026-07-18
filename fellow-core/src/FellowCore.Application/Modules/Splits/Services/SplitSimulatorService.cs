using FellowCore.Application.Common;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Splits.DTOs;
using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.Splits.Services;

public interface ISplitSimulatorService
{
    Task<SimulateSplitResponse> SimulateAsync(Guid tenantId, SimulateSplitRequest request);
}

public class SplitSimulatorService(
    IPricingService pricingService,
    IProviderCostService providerCostService,
    ISplitRuleRepository splitRuleRepository,
    ISellerRepository sellerRepository) : ISplitSimulatorService
{
    // Sprint 1.5: limite arquitetural (50). Tier-based limits virão na Sprint 2.
    private const int MaxSplitRecipientsHardCap = 50;

    /// <summary>
    /// Decide o provider que processaria essa transação — replica a lógica do RailRouter
    /// sem precisar carregar Tenant.Config (esta é uma simulação, não fluxo real). Se o
    /// preferred provider do seller é compatível com o payment type, usa ele; senão cai
    /// no default por payment type (PIX→OPENPIX, demais→STRIPE).
    /// </summary>
    private static PaymentProvider ResolveProviderForPaymentType(PaymentType paymentType, Seller seller)
    {
        if (seller.PreferredProvider.HasValue && IsProviderCompatible(seller.PreferredProvider.Value, paymentType))
            return seller.PreferredProvider.Value;

        return paymentType switch
        {
            PaymentType.PIX => PaymentProvider.OPENPIX,
            PaymentType.CREDIT_CARD or PaymentType.DEBIT_CARD => PaymentProvider.STRIPE,
            PaymentType.BOLETO => PaymentProvider.STRIPE,
            _ => PaymentProvider.STRIPE
        };
    }

    private static bool IsProviderCompatible(PaymentProvider provider, PaymentType paymentType) =>
        (provider, paymentType) switch
        {
            (PaymentProvider.OPENPIX, PaymentType.PIX) => true,
            (PaymentProvider.STRIPE, PaymentType.CREDIT_CARD) => true,
            (PaymentProvider.STRIPE, PaymentType.DEBIT_CARD) => true,
            (PaymentProvider.STRIPE, PaymentType.BOLETO) => true,
            _ => false
        };

    public async Task<SimulateSplitResponse> SimulateAsync(Guid tenantId, SimulateSplitRequest request)
    {
        var warnings = new List<string>();

        // Validate seller exists
        var seller = await sellerRepository.GetByIdAsync(tenantId, request.SellerId)
            ?? throw new NotFoundException("Seller", request.SellerId.ToString());

        // Calculate platform fee
        var feeResult = await pricingService.CalculatePlatformFeeAsync(
            tenantId, request.SellerId, request.PaymentType, request.Installments, request.Amount);

        // Calculate provider cost — usa o mesmo critério do RailRouter pra escolher
        // o provedor que efetivamente processaria essa transação. Antes assumíamos
        // `seller.PreferredProvider ?? STRIPE` cego, o que dava (STRIPE, PIX) — combinação
        // que não tem schedule cadastrado e retornava custo zero silenciosamente.
        var provider = ResolveProviderForPaymentType(request.PaymentType, seller);
        decimal providerCost = await providerCostService.CalculateProviderCostAsync(
            provider, request.PaymentType, request.Amount);

        decimal platformFee = feeResult.PlatformFeeAmount;
        decimal netAmount = feeResult.SellerNetAmount;
        decimal margin = platformFee - providerCost;

        if (margin < 0)
            warnings.Add($"Margem negativa: plataforma subsidia R${Math.Abs(margin):F2} por transação.");

        // Resolve recipients from split rule or explicit splits
        var recipientInputs = new List<SplitRecipientInput>();

        if (request.SplitRuleId.HasValue)
        {
            if (request.Splits?.Count > 0)
                throw new BusinessException("Split.ConflictingConfig", "Não é possível enviar splits e splitRuleId ao mesmo tempo.");

            var rule = await splitRuleRepository.GetByIdWithRecipientsAsync(tenantId, request.SplitRuleId.Value)
                ?? throw new NotFoundException("SplitRule", request.SplitRuleId.Value.ToString());

            int priority = 0;
            foreach (var recipient in rule.Recipients)
            {
                recipientInputs.Add(new SplitRecipientInput(
                    recipient.SellerId, recipient.FixedAmount, recipient.Percentage, priority++));
            }
        }
        else if (request.Splits?.Count > 0)
        {
            int priority = 0;
            foreach (var split in request.Splits)
            {
                recipientInputs.Add(new SplitRecipientInput(
                    split.SellerId, split.Amount, split.Percentage, priority++));
            }
        }
        else
        {
            // No splits — all goes to primary seller
            return new SimulateSplitResponse(
                GrossAmount: request.Amount,
                PlatformFee: platformFee,
                ProviderCostEstimate: providerCost,
                PlatformMarginEstimate: margin,
                NetAmount: netAmount,
                Recipients: [],
                PrimaryResidual: new SimulatedPrimaryResidual(request.SellerId, netAmount),
                RoundingAdjustment: 0,
                Warnings: warnings);
        }

        // Sprint 1.5: limite arquitetural (50). Sprint 2 vai introduzir tier-based limits.
        if (recipientInputs.Count > MaxSplitRecipientsHardCap)
        {
            warnings.Add($"Limite arquitetural: máximo {MaxSplitRecipientsHardCap} recipients por TX. Atual: {recipientInputs.Count}.");
        }

        // ─── Cálculo da divisão ──────────────────────────────────────────────────
        //
        // Não usamos splitCalculationService.Calculate aqui porque ele foi escrito
        // assumindo PlatformFee=0 (a pipeline real extrai a fee antes — ver
        // SplitProcessor.cs:96-100). Quando passamos fee != 0, a lógica dele aloca
        // fee ao "sorted[0]" (primeiro recipient externo), o que está errado: o
        // primary seller (request.SellerId) é quem deveria pagar (em PRIMARY) ou
        // dividir proporcionalmente (em PROPORTIONAL), mas ele nem aparece na
        // lista de recipients. Reimplementamos o cálculo aqui pra dar o resultado
        // semanticamente correto pra simulação.
        //
        // Regras por política:
        //   PRIMARY_SELLER_PAYS_FEES   → recipients recebem o gross cheio; primary paga TODA a taxa.
        //   PROPORTIONAL_TO_RECIPIENTS → cada um (incluindo primary) absorve sua fração da taxa
        //                                proporcional ao gross.
        //   PLATFORM_ABSORBS           → recipients e primary recebem o gross; plataforma engole a taxa.

        var policy = request.FeeAllocationPolicy ?? FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES;

        // 1. Calcula gross share de cada recipient da lista (em cima do request.Amount).
        //    Lista pode incluir o próprio primary seller — comum em rules (ex: 80% primary,
        //    20% parceiro). Nesse caso o primary não tem "residual" — recebe exatamente
        //    sua share configurada.
        decimal allocatedSum = 0m;
        var grossByRecipient = new List<(Guid SellerId, decimal Gross)>();
        foreach (var input in recipientInputs)
        {
            decimal gross;
            if (input.FixedAmount.HasValue)
                gross = Math.Min(input.FixedAmount.Value, request.Amount - allocatedSum);
            else if (input.Percentage.HasValue)
                gross = RoundingPolicy.Round(request.Amount * input.Percentage.Value / 100m);
            else
                gross = 0m;

            allocatedSum += gross;
            grossByRecipient.Add((input.SellerId, gross));
        }

        // 2. Primary share:
        //    - Se o primary aparece na lista (rules tipicamente incluem ele), usa o gross dele.
        //    - Senão, primary fica com o residual do gross (request.Amount - soma_explicitos).
        bool primaryInRecipients = grossByRecipient.Any(g => g.SellerId == request.SellerId);
        decimal primaryGross = primaryInRecipients
            ? grossByRecipient.First(g => g.SellerId == request.SellerId).Gross
            : Math.Max(0m, request.Amount - allocatedSum);

        // Soma total a ser distribuída (recipients explícitos + residual do primary se ele
        // não estiver na lista). Em rules bem-formadas isso bate com request.Amount.
        decimal totalDistributed = primaryInRecipients ? allocatedSum : allocatedSum + primaryGross;
        if (allocatedSum > request.Amount)
            warnings.Add($"A soma dos splits ({allocatedSum:F2}) excede o valor da transação ({request.Amount:F2}).");
        else if (totalDistributed < request.Amount)
            warnings.Add($"A soma dos splits ({totalDistributed:F2}) é menor que o valor da transação ({request.Amount:F2}). Residual de {request.Amount - totalDistributed:F2} fica sem destinação.");

        // 3. Distribui a fee conforme a política.
        decimal feeForPrimary;
        var feeByRecipient = new Dictionary<Guid, decimal>();

        switch (policy)
        {
            case FeeAllocationPolicy.PRIMARY_SELLER_PAYS_FEES:
                // Primary paga TODA a taxa, recipients externos recebem cheio.
                feeForPrimary = platformFee;
                foreach (var (sellerId, _) in grossByRecipient)
                    feeByRecipient[sellerId] = sellerId == request.SellerId ? platformFee : 0m;
                break;

            case FeeAllocationPolicy.PROPORTIONAL_TO_RECIPIENTS:
                // Cada participante (recipients + primary) absorve fração proporcional ao seu gross.
                if (request.Amount > 0)
                {
                    decimal allocatedFee = 0m;
                    decimal feeForPrimaryFromLoop = 0m;
                    foreach (var (sellerId, gross) in grossByRecipient)
                    {
                        var f = RoundingPolicy.Round(platformFee * gross / request.Amount);
                        feeByRecipient[sellerId] = f;
                        allocatedFee += f;
                        if (sellerId == request.SellerId) feeForPrimaryFromLoop = f;
                    }

                    if (primaryInRecipients)
                    {
                        // Resíduo do arredondamento entra no primary (que já está no dict).
                        decimal residual = platformFee - allocatedFee;
                        feeByRecipient[request.SellerId] = feeForPrimaryFromLoop + residual;
                        feeForPrimary = feeByRecipient[request.SellerId];
                    }
                    else
                    {
                        // Primary tem sua fração proporcional ao residual + qualquer arredondamento sobrando.
                        feeForPrimary = RoundingPolicy.Round(platformFee * primaryGross / request.Amount);
                        decimal totalFeeAccounted = allocatedFee + feeForPrimary;
                        if (totalFeeAccounted != platformFee)
                            feeForPrimary += platformFee - totalFeeAccounted;
                    }
                }
                else
                {
                    feeForPrimary = platformFee;
                    foreach (var (sellerId, _) in grossByRecipient) feeByRecipient[sellerId] = 0m;
                }
                break;

            case FeeAllocationPolicy.PLATFORM_ABSORBS:
                feeForPrimary = 0m;
                foreach (var (sellerId, _) in grossByRecipient) feeByRecipient[sellerId] = 0m;
                break;

            default:
                feeForPrimary = platformFee;
                foreach (var (sellerId, _) in grossByRecipient)
                    feeByRecipient[sellerId] = sellerId == request.SellerId ? platformFee : 0m;
                break;
        }

        // 4. Monta os DTOs de recipient (excluindo o primary; ele vai em PrimaryResidual).
        var recipients = grossByRecipient
            .Where(r => r.SellerId != request.SellerId)
            .Select(r =>
            {
                var fee = feeByRecipient.TryGetValue(r.SellerId, out var f) ? f : 0m;
                return new SimulatedRecipient(
                    SellerId: r.SellerId,
                    GrossShare: r.Gross,
                    FeeShare: fee,
                    NetShare: r.Gross - fee,
                    Type: "RECIPIENT_SHARE");
            })
            .ToList();

        decimal primaryNet = primaryGross - feeForPrimary;

        // 5. NetAmount reportado: o que efetivamente vai pros sellers (recipients + primary).
        //    Em PLATFORM_ABSORBS isso é igual ao gross; nas outras políticas é gross - fee.
        decimal effectiveNet = policy == FeeAllocationPolicy.PLATFORM_ABSORBS
            ? request.Amount
            : request.Amount - platformFee;

        if (policy == FeeAllocationPolicy.PLATFORM_ABSORBS && platformFee > 0)
            warnings.Add($"Plataforma absorvendo R${platformFee:F2} de taxa nesta simulação.");

        return new SimulateSplitResponse(
            GrossAmount: request.Amount,
            PlatformFee: platformFee,
            ProviderCostEstimate: providerCost,
            PlatformMarginEstimate: margin,
            NetAmount: effectiveNet,
            Recipients: recipients,
            PrimaryResidual: new SimulatedPrimaryResidual(request.SellerId, primaryNet),
            RoundingAdjustment: 0m,
            Warnings: warnings);
    }
}
