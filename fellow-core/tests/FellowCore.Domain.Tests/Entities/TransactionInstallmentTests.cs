using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

/// <summary>
/// Cobre geração e ciclo de vida de installments. Garantia central:
/// soma de TX.Installments parcelas == NetAmount original (sem perda de centavos).
/// </summary>
public class TransactionInstallmentTests
{
    private static Transaction BuildCreditTx(decimal amount, decimal net, int installments)
    {
        var result = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: amount,
            paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE,
            installments: installments,
            feeAmount: amount - net,
            netAmount: net,
            expectedSettlementDate: null,
            providerTxId: $"pi_{Guid.NewGuid():N}",
            sellerId: Guid.NewGuid());
        return result.Value;
    }

    [Fact]
    public void CreateForTransaction_CreditCard1x_GeneratesSingleInstallmentAt30Days()
    {
        var tx = BuildCreditTx(amount: 100m, net: 95m, installments: 1);
        var capturedAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc);

        var installments = TransactionInstallment.CreateForTransaction(tx, capturedAt);

        installments.Should().HaveCount(1);
        installments[0].Number.Should().Be(1);
        installments[0].TotalCount.Should().Be(1);
        installments[0].NetAmount.Should().Be(95m);
        installments[0].ExpectedReleaseDate.Should().Be(capturedAt.AddDays(30));
        installments[0].Status.Should().Be(SettlementStatus.PENDING);
    }

    [Fact]
    public void CreateForTransaction_CreditCard6x_GeneratesSixMonthlyInstallments()
    {
        // Caso Bruce: TX de R$ 600 (net R$ 561) parcelada 6x → 6 parcelas de ~R$ 93,50
        var tx = BuildCreditTx(amount: 600m, net: 561m, installments: 6);
        var capturedAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc);

        var installments = TransactionInstallment.CreateForTransaction(tx, capturedAt);

        installments.Should().HaveCount(6);
        installments[0].ExpectedReleaseDate.Should().Be(capturedAt.AddDays(30));
        installments[5].ExpectedReleaseDate.Should().Be(capturedAt.AddDays(180));

        // Soma das parcelas == net original (zero drift de centavos)
        installments.Sum(i => i.NetAmount).Should().Be(561m);

        // Cada parcela tem TotalCount = 6 e Number sequencial
        installments.Select(i => i.Number).Should().ContainInOrder(1, 2, 3, 4, 5, 6);
        installments.Should().AllSatisfy(i => i.TotalCount.Should().Be(6));
    }

    [Fact]
    public void CreateForTransaction_CentRemainderGoesToLastInstallment()
    {
        // R$ 100 / 6 = 16,6666… → 5 parcelas de R$ 16,66 + última de R$ 16,70.
        // Soma exata: 5 * 16.66 + 16.70 = 83.30 + 16.70 = 100.00
        var tx = BuildCreditTx(amount: 105m, net: 100m, installments: 6);
        var capturedAt = DateTime.UtcNow;

        var installments = TransactionInstallment.CreateForTransaction(tx, capturedAt);

        installments.Take(5).Should().AllSatisfy(i => i.NetAmount.Should().Be(16.66m));
        installments[5].NetAmount.Should().Be(16.70m);
        installments.Sum(i => i.NetAmount).Should().Be(100m);
    }

    [Fact]
    public void CreateForTransaction_NoNetAmount_Throws()
    {
        // Construímos manualmente uma TX sem NetAmount pra cobrir defesa
        var bad = Transaction.Create(
            tenantId: Guid.NewGuid(),
            amount: 100m,
            paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: null,
            netAmount: null, // ← inválido pro contexto
            expectedSettlementDate: null,
            providerTxId: "pi_x").Value;

        var act = () => TransactionInstallment.CreateForTransaction(bad, DateTime.UtcNow);

        act.Should().Throw<ArgumentException>().WithMessage("*NetAmount*");
    }

    [Fact]
    public void MarkSettled_FromPending_TransitionsToSettled()
    {
        var tx = BuildCreditTx(100m, 95m, 1);
        var inst = TransactionInstallment.CreateForTransaction(tx, DateTime.UtcNow)[0];
        var before = DateTime.UtcNow;

        var result = inst.MarkSettled();

        result.IsSuccess.Should().BeTrue();
        inst.Status.Should().Be(SettlementStatus.SETTLED);
        inst.SettledAt.Should().NotBeNull();
        inst.SettledAt!.Value.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void MarkSettled_AlreadySettled_IsIdempotent()
    {
        var tx = BuildCreditTx(100m, 95m, 1);
        var inst = TransactionInstallment.CreateForTransaction(tx, DateTime.UtcNow)[0];
        inst.MarkSettled();
        var firstSettledAt = inst.SettledAt;

        var result = inst.MarkSettled(); // 2nd call

        result.IsSuccess.Should().BeTrue();
        inst.SettledAt.Should().Be(firstSettledAt, "second call não deve modificar SettledAt");
    }

    [Fact]
    public void Cancel_FromPending_TransitionsToCanceled()
    {
        var tx = BuildCreditTx(100m, 95m, 1);
        var inst = TransactionInstallment.CreateForTransaction(tx, DateTime.UtcNow)[0];
        var before = DateTime.UtcNow;

        var result = inst.Cancel();

        result.IsSuccess.Should().BeTrue();
        inst.Status.Should().Be(SettlementStatus.CANCELED);
        inst.UpdatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Cancel_AlreadyCanceled_IsIdempotent()
    {
        var tx = BuildCreditTx(100m, 95m, 1);
        var inst = TransactionInstallment.CreateForTransaction(tx, DateTime.UtcNow)[0];
        inst.Cancel();
        var firstUpdatedAt = inst.UpdatedAt;

        var result = inst.Cancel(); // 2nd call

        result.IsSuccess.Should().BeTrue();
        inst.UpdatedAt.Should().Be(firstUpdatedAt, "no-op não pode reescrever UpdatedAt");
    }

    [Fact]
    public void Cancel_AlreadySettled_Fails()
    {
        // Dinheiro já liberou pro WALLET; reversão precisa ser via ledger, não Cancel.
        var tx = BuildCreditTx(100m, 95m, 1);
        var inst = TransactionInstallment.CreateForTransaction(tx, DateTime.UtcNow)[0];
        inst.MarkSettled();

        var result = inst.Cancel();

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Installment.AlreadySettled");
        inst.Status.Should().Be(SettlementStatus.SETTLED, "Cancel não deve mexer no Status quando falha");
    }

    [Fact]
    public void MarkSettled_AfterCancel_Fails()
    {
        // Após cancel (refund), settlement processor não pode liberar a parcela.
        var tx = BuildCreditTx(100m, 95m, 1);
        var inst = TransactionInstallment.CreateForTransaction(tx, DateTime.UtcNow)[0];
        inst.Cancel();

        var result = inst.MarkSettled();

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Installment.AlreadyCanceled");
        inst.Status.Should().Be(SettlementStatus.CANCELED);
        inst.SettledAt.Should().BeNull();
    }

    [Fact]
    public void CreateForTransaction_AdvanceMode_GeneratesSinglePayoutAtD30()
    {
        // TX 6x crédito, R$ 600 (net R$ 561), seller optou por antecipação.
        // Plano cobra 3.5% advance fee = R$ 19,64. Seller recebe R$ 541,36 em D+30.
        var tx = BuildCreditTx(amount: 600m, net: 561m, installments: 6);
        var capturedAt = new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc);
        // Simula o flow real: service chama MarkAsAdvanceSettlement antes de CreateForTransaction
        var advanceFee = 19.64m;
        tx.MarkAsAdvanceSettlement(advanceFee);

        var installments = TransactionInstallment.CreateForTransaction(tx, capturedAt);

        installments.Should().HaveCount(1, "ADVANCE = 1 única parcela, independente de Installments do customer");
        installments[0].Number.Should().Be(1);
        installments[0].TotalCount.Should().Be(1);
        installments[0].NetAmount.Should().Be(541.36m, "net seller = NetAmount - advanceFee");
        installments[0].ExpectedReleaseDate.Should().Be(capturedAt.AddDays(30), "ADVANCE sempre D+30");
    }

    [Fact]
    public void CreateForTransaction_AdvanceMode_FeeExceedsNet_Throws()
    {
        // Defesa: fee >= net = configuração inválida (plano com >100% AdvancePercentFee).
        var tx = BuildCreditTx(amount: 100m, net: 50m, installments: 1);
        tx.MarkAsAdvanceSettlement(60m); // 60 > 50

        var act = () => TransactionInstallment.CreateForTransaction(tx, DateTime.UtcNow);

        act.Should().Throw<ArgumentException>().WithMessage("*AdvanceFee*");
    }

    [Fact]
    public void Transaction_MarkAsAdvanceSettlement_AlreadyAdvanced_Fails()
    {
        var tx = BuildCreditTx(100m, 95m, 1);
        tx.MarkAsAdvanceSettlement(5m);

        var result = tx.MarkAsAdvanceSettlement(5m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Transaction.AlreadyAdvanced");
    }

    [Fact]
    public void Transaction_MarkAsAdvanceSettlement_NegativeFee_Fails()
    {
        var tx = BuildCreditTx(100m, 95m, 1);

        var result = tx.MarkAsAdvanceSettlement(-1m);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Transaction.InvalidAdvanceFee");
        tx.SettlementMode.Should().Be(SettlementMode.INSTALLMENT, "estado não muda em failure");
    }

    [Fact]
    public void Transaction_Create_PersistsAdvanceOptInOverride()
    {
        // Per-TX override: customer/checkout decide caso a caso.
        var resultTrue = Transaction.Create(
            tenantId: Guid.NewGuid(), amount: 100m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 6, feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: null, providerTxId: "pi_x", advanceOptIn: true);
        var resultFalse = Transaction.Create(
            tenantId: Guid.NewGuid(), amount: 100m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 6, feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: null, providerTxId: "pi_y", advanceOptIn: false);
        var resultNull = Transaction.Create(
            tenantId: Guid.NewGuid(), amount: 100m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 6, feeAmount: 5m, netAmount: 95m,
            expectedSettlementDate: null, providerTxId: "pi_z"); // sem override

        resultTrue.Value.AdvanceOptIn.Should().BeTrue();
        resultFalse.Value.AdvanceOptIn.Should().BeFalse();
        resultNull.Value.AdvanceOptIn.Should().BeNull();
    }

    // Sprint 1.5: PricingPlan_SetAdvancePercentFee tests deletados — entidade PricingPlan
    // foi removida. AdvancePercentFee virou config global em TierPricingOptions.AdvancePercentFee.

    [Fact]
    public void CreateForTransaction_HandlesInstallmentsZeroAsOne()
    {
        // Defesa: se installments vier 0, trata como 1 (mesmo guard que Transaction.Create).
        var tx = BuildCreditTx(100m, 95m, 1); // valid TX
        // override via reflection — simula caller chamando direto com 0 (improvável mas defensivo)
        typeof(Transaction).GetProperty("Installments")!.SetValue(tx, 0);

        var installments = TransactionInstallment.CreateForTransaction(tx, DateTime.UtcNow);

        installments.Should().HaveCount(1);
        installments[0].NetAmount.Should().Be(95m);
    }
}
