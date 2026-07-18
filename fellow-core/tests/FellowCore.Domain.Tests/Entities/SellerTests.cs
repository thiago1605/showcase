using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class SellerTests
{
    private static Seller CreateSeller(decimal feePixIn = 1.5m, decimal feeDebit = 2.0m,
        decimal feeCreditCash = 4.5m, decimal feeCreditInstallment = 6.5m)
    {
        var seller = Seller.Create(
            tenantId: Guid.NewGuid(),
            legalName: "Loja Teste",
            document: "12345678901",
            email: "teste@loja.com",
            webhookSecret: "secret-32-chars-webhook-secret!!");
        // Seller uses defaults; we just use the public field values
        return seller;
    }

    [Fact]
    public void Create_ShouldSetStatusAsActive()
    {
        var seller = Seller.Create(
            Guid.NewGuid(), "Loja Teste", "12345678901", "loja@teste.com", "secret-webhook-32chars-long!!");

        seller.Status.Should().Be(SellerStatus.ACTIVE);
    }

    [Fact]
    public void Activate_ShouldSetStatusAsActive()
    {
        var seller = CreateSeller();
        seller.Suspend();

        seller.Activate();

        seller.Status.Should().Be(SellerStatus.ACTIVE);
    }

    [Fact]
    public void Suspend_ShouldSetStatusAsSuspended()
    {
        var seller = CreateSeller();

        seller.Suspend();

        seller.Status.Should().Be(SellerStatus.SUSPENDED);
    }

    [Theory]
    [InlineData(100.0, 1.50, 1.50)]   // 1.5% de R$100 = R$1.50
    [InlineData(200.0, 1.50, 3.00)]   // 1.5% de R$200 = R$3.00
    [InlineData(100.0, 0.0, 0.0)]     // taxa zero → sem fee
    public void CalculateFee_ForPix_ShouldReturnCorrectFee(decimal amount, decimal feePercent, decimal expectedFee)
    {
        var seller = Seller.Create(
            Guid.NewGuid(), "Loja", "12345678901", "loja@teste.com", "secret-webhook-32chars-long!!",
            feePixIn: feePercent);

        var (feeAmount, netAmount) = seller.CalculateFee(PaymentType.PIX, 1, amount);

        feeAmount.Should().Be(expectedFee);
        netAmount.Should().Be(amount - expectedFee);
    }

    [Fact]
    public void CalculateFee_ForCreditCash_ShouldUseCashRate()
    {
        var seller = Seller.Create(
            Guid.NewGuid(), "Loja", "12345678901", "loja@teste.com", "secret-webhook-32chars-long!!");

        var (feeAmount, netAmount) = seller.CalculateFee(PaymentType.CREDIT_CARD, 1, 100m);

        feeAmount.Should().Be(4.5m);
        netAmount.Should().Be(95.5m);
    }

    [Fact]
    public void CalculateFee_ForCreditInstallment_ShouldUseInstallmentRate()
    {
        var seller = Seller.Create(
            Guid.NewGuid(), "Loja", "12345678901", "loja@teste.com", "secret-webhook-32chars-long!!");

        var (feeAmount, netAmount) = seller.CalculateFee(PaymentType.CREDIT_CARD, 3, 100m);

        feeAmount.Should().Be(6.5m);
        netAmount.Should().Be(93.5m);
    }
}
