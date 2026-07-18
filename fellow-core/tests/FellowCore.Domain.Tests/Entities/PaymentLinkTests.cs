using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class PaymentLinkTests
{
    [Fact]
    public void Create_ShouldSetAllProperties()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var link = PaymentLink.Create(tenantId, 250m, PaymentType.PIX, sellerId: sellerId, description: "Produto X");

        link.Id.Should().NotBeEmpty();
        link.TenantId.Should().Be(tenantId);
        link.SellerId.Should().Be(sellerId);
        link.Amount.Should().Be(250m);
        link.PaymentType.Should().Be(PaymentType.PIX);
        link.Installments.Should().Be(1);
        // MaxUses agora é nullable: null = ilimitado (não Set por padrão).
        // Migration MakePaymentLinkMaxUsesNullable estabeleceu essa semântica.
        link.MaxUses.Should().BeNull();
        link.UsageCount.Should().Be(0);
        link.Active.Should().BeTrue();
        link.Description.Should().Be("Produto X");
        link.Token.Should().NotBeNullOrEmpty();
        link.Token.Should().HaveLength(32);
        link.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldGenerateUniqueTokens()
    {
        var link1 = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.CREDIT_CARD);
        var link2 = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.CREDIT_CARD);

        link1.Token.Should().NotBe(link2.Token);
    }

    [Fact]
    public void Create_ShouldSetInstallmentsToMinimumOne()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.CREDIT_CARD, installments: 0);

        link.Installments.Should().Be(1);
    }

    [Fact]
    public void Create_WithMaxUsesZero_ShouldStoreAsNullUnlimited()
    {
        // maxUses <= 0 vira null (ilimitado) — sentinela "0" antes virava 1, agora
        // é tratado como "sem limite" pra alinhar com a coluna nullable no DB.
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, maxUses: 0);

        link.MaxUses.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldAllowMultipleInstallments()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 1200m, PaymentType.CREDIT_CARD, installments: 12);

        link.Installments.Should().Be(12);
    }

    [Fact]
    public void Create_ShouldSetExpiresAt()
    {
        var expiry = DateTime.UtcNow.AddDays(7);

        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, expiresAt: expiry);

        link.ExpiresAt.Should().Be(expiry);
    }

    [Fact]
    public void Create_ShouldAllowNullSellerId()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX);

        link.SellerId.Should().BeNull();
    }

    [Fact]
    public void IsValid_ShouldReturnTrue_WhenActiveAndNotExpiredAndNotExhausted()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, maxUses: 5);

        link.IsValid().Should().BeTrue();
    }

    [Fact]
    public void IsValid_ShouldReturnFalse_WhenDeactivated()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX);

        link.Deactivate();

        link.IsValid().Should().BeFalse();
    }

    [Fact]
    public void IsValid_ShouldReturnFalse_WhenExpired()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, expiresAt: DateTime.UtcNow.AddMinutes(-1));

        link.IsValid().Should().BeFalse();
    }

    [Fact]
    public void IsValid_ShouldReturnFalse_WhenMaxUsesReached()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, maxUses: 1);

        link.RecordUsage();

        link.IsValid().Should().BeFalse();
    }

    [Fact]
    public void RecordUsage_ShouldIncrementUsageCount()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, maxUses: 3);

        link.RecordUsage();

        link.UsageCount.Should().Be(1);
        link.Active.Should().BeTrue();
    }

    [Fact]
    public void RecordUsage_ShouldDeactivateLink_WhenMaxUsesReached()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, maxUses: 2);

        link.RecordUsage();
        link.RecordUsage();

        link.UsageCount.Should().Be(2);
        link.Active.Should().BeFalse();
    }

    [Fact]
    public void RecordUsage_ShouldDeactivateOnSingleUse()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 50m, PaymentType.BOLETO, maxUses: 1);

        link.RecordUsage();

        link.UsageCount.Should().Be(1);
        link.Active.Should().BeFalse();
        link.IsValid().Should().BeFalse();
    }

    [Fact]
    public void Deactivate_ShouldSetActiveToFalse()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX);

        link.Deactivate();

        link.Active.Should().BeFalse();
    }

    [Fact]
    public void IsValid_ShouldReturnTrue_WhenExpiresAtIsInFuture()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.PIX, expiresAt: DateTime.UtcNow.AddDays(30));

        link.IsValid().Should().BeTrue();
    }

    [Fact]
    public void MultipleUsages_ShouldNotExceedMaxUses_InValidityCheck()
    {
        var link = PaymentLink.Create(Guid.NewGuid(), 100m, PaymentType.CREDIT_CARD, maxUses: 3);

        link.RecordUsage();
        link.RecordUsage();
        link.IsValid().Should().BeTrue();

        link.RecordUsage();
        link.IsValid().Should().BeFalse();
    }
}
