using FluentAssertions;
using FellowCore.Application.Modules.Transactions.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Tests.Services;

/// <summary>
/// Trava as invariantes da POLÍTICA GROSS INTEGRAL:
///   1. Cliente recebe gross integral (sempre).
///   2. Seller paga GROSS INTEGRAL — débito da carteira = valor que cliente
///      recebe. Refund parcial: débito = valor parcial (não net + taxa).
///   3. Plataforma fica com margem mesmo em refund (necessária pra cobrir
///      custo do provider que ela paga e não recupera).
///   4. SellerNetPortion + PlatformFeeWithheld = SellerTotalDebit (= gross),
///      por construção. São campos informativos pra mostrar a decomposição
///      ao seller, mas o que efetivamente debita é o gross.
/// </summary>
public class RefundCalculatorTests
{
    /// <summary>
    /// TX padrão de exemplo (espelhando o caso real do usuário: R$ 150 cartão
    /// de crédito, taxa Fellow Pay R$ 7,82, custo Stripe R$ 6,38, margem R$ 1,44).
    /// </summary>
    private static Transaction CreateSampleTransaction(decimal amount = 150m)
    {
        var tx = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: amount,
            paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 6.75m,
            netAmount: amount - 6.75m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_test",
            sellerId: Guid.NewGuid()
        ).Value;

        // Campos modernos (PricingPlan) — setados via reflection já que o factory
        // não expõe ainda. Espelha o que o pipeline real produz após o IPricingService.
        typeof(Transaction).GetProperty(nameof(Transaction.PlatformFeeAmount))!.SetValue(tx, 7.82m);
        typeof(Transaction).GetProperty(nameof(Transaction.ProviderCostAmount))!.SetValue(tx, 6.38m);
        typeof(Transaction).GetProperty(nameof(Transaction.PlatformMarginAmount))!.SetValue(tx, 1.44m);
        return tx;
    }

    [Fact]
    public void FullRefund_DebitsSellerGrossIntegral()
    {
        var tx = CreateSampleTransaction(amount: 150m);

        var breakdown = RefundCalculator.Calculate(tx, refundAmount: 150m);

        // Cliente recebe gross
        breakdown.CustomerRefund.Should().Be(150m);

        // Seller paga GROSS INTEGRAL — mesmo valor que cliente recebe
        breakdown.SellerTotalDebit.Should().Be(150m);

        // Decomposição informativa: líquido + taxa = gross
        breakdown.SellerNetPortion.Should().Be(143.25m);  // o que ele recebeu na captura
        breakdown.PlatformFeeWithheld.Should().Be(6.75m); // taxa Fellow Pay que ele já pagou
        (breakdown.SellerNetPortion + breakdown.PlatformFeeWithheld)
            .Should().Be(breakdown.SellerTotalDebit);

        // Custo Stripe proporcional (informativo interno)
        breakdown.ProviderCostPortion.Should().Be(6.38m);
    }

    [Fact]
    public void PartialRefund_DebitsSellerProportionalGross()
    {
        var tx = CreateSampleTransaction(amount: 150m);

        // Refund de R$ 50 = 1/3 da TX → seller debita R$ 50 (não 47,75, não 49,88)
        var breakdown = RefundCalculator.Calculate(tx, refundAmount: 50m);

        breakdown.CustomerRefund.Should().Be(50m);
        breakdown.SellerTotalDebit.Should().Be(50m); // gross parcial

        // Decomposição informativa
        breakdown.SellerNetPortion.Should().Be(47.75m);  // líquido proporcional
        breakdown.PlatformFeeWithheld.Should().Be(2.25m); // taxa proporcional
        (breakdown.SellerNetPortion + breakdown.PlatformFeeWithheld)
            .Should().Be(breakdown.SellerTotalDebit);
    }

    [Fact]
    public void TwoPartialRefunds_SumExactlyToFullGross()
    {
        // R$ 50 + R$ 100 = R$ 150 — soma dos debits DEVE ser exata (não aproximada)
        // porque cada um é o próprio refundAmount (sem rounding intermediário).
        var tx = CreateSampleTransaction(amount: 150m);

        var first = RefundCalculator.Calculate(tx, refundAmount: 50m);
        var second = RefundCalculator.Calculate(tx, refundAmount: 100m);

        (first.SellerTotalDebit + second.SellerTotalDebit).Should().Be(150m);
    }

    [Fact]
    public void LegacyTransaction_WithoutModernFeeFields_StillDebitsGross()
    {
        // TX legada sem PlatformFeeAmount/ProviderCostAmount. Política gross
        // continua válida — seller paga o valor cheio do refund. Só a decomposição
        // informativa fica menos detalhada.
        var tx = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: 100m,
            paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 5m,
            netAmount: 95m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_test",
            sellerId: Guid.NewGuid()
        ).Value;

        var breakdown = RefundCalculator.Calculate(tx, refundAmount: 100m);

        breakdown.SellerTotalDebit.Should().Be(100m);    // gross sempre
        breakdown.SellerNetPortion.Should().Be(95m);     // do NetAmount disponível
        breakdown.PlatformFeeWithheld.Should().Be(5m);   // = gross - net
        breakdown.ProviderCostPortion.Should().Be(0m);   // sem dados, info zero
    }

    [Fact]
    public void MaxRefundable_ReflectsAlreadyRefundedAmount()
    {
        var tx = CreateSampleTransaction(amount: 150m);
        typeof(Transaction).GetProperty(nameof(Transaction.RefundedAmount))!.SetValue(tx, 50m);

        var breakdown = RefundCalculator.Calculate(tx, refundAmount: 100m);

        breakdown.MaxRefundableAmount.Should().Be(100m); // 150 - 50 já refundado
    }

    [Fact]
    public void RealUserScenario_R300_DebitsR300()
    {
        // Cenário real do print do usuário: TX R$ 300 com NetAmount R$ 286,50
        // e PlatformFeeAmount R$ 13,50. Antes do fix, o débito era R$ 298,86
        // (net + providerCost). O usuário pediu: deve sair R$ 300.
        var tx = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: 300m,
            paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 13.50m,
            netAmount: 286.50m,
            expectedSettlementDate: DateTime.UtcNow.AddDays(30),
            providerTxId: "pi_test_300",
            sellerId: Guid.NewGuid()
        ).Value;
        typeof(Transaction).GetProperty(nameof(Transaction.PlatformFeeAmount))!.SetValue(tx, 13.50m);
        typeof(Transaction).GetProperty(nameof(Transaction.ProviderCostAmount))!.SetValue(tx, 12.36m);

        var breakdown = RefundCalculator.Calculate(tx, refundAmount: 300m);

        breakdown.SellerTotalDebit.Should().Be(300m); // GROSS — não 298,86
        breakdown.SellerNetPortion.Should().Be(286.50m);
        breakdown.PlatformFeeWithheld.Should().Be(13.50m); // = 300 - 286,50
    }
}
