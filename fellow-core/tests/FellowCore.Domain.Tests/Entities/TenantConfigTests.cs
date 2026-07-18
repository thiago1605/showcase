using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Tests.Entities;

public class TenantConfigTests
{
    [Fact]
    public void Create_ShouldSetDefaultValues()
    {
        var tenantId = Guid.NewGuid();

        var config = TenantConfig.Create(tenantId);

        config.Id.Should().NotBeEmpty();
        config.TenantId.Should().Be(tenantId);
        config.ActiveCreditProvider.Should().Be(PaymentProvider.STRIPE);
        config.ActivePixProvider.Should().Be(PaymentProvider.OPENPIX);
        config.EnableAntifraud.Should().BeFalse();
        config.AutomaticCapture.Should().BeTrue();
        config.StripeChargeMode.Should().Be(StripeChargeMode.DESTINATION_CHARGE);
    }

    [Fact]
    public void SetActivePixProvider_ShouldUpdateProvider()
    {
        var config = TenantConfig.Create(Guid.NewGuid());

        config.SetActivePixProvider(PaymentProvider.STRIPE);

        config.ActivePixProvider.Should().Be(PaymentProvider.STRIPE);
    }

    [Fact]
    public void SetActiveCreditProvider_ShouldUpdateProvider()
    {
        var config = TenantConfig.Create(Guid.NewGuid());

        config.SetActiveCreditProvider(PaymentProvider.OPENPIX);

        config.ActiveCreditProvider.Should().Be(PaymentProvider.OPENPIX);
    }

    [Fact]
    public void SetStripeChargeMode_ShouldUpdateMode()
    {
        var config = TenantConfig.Create(Guid.NewGuid());

        config.SetStripeChargeMode(StripeChargeMode.DIRECT_CHARGE);

        config.StripeChargeMode.Should().Be(StripeChargeMode.DIRECT_CHARGE);
    }

    [Fact]
    public void SetActivePixProvider_ShouldAllowSandbox()
    {
        var config = TenantConfig.Create(Guid.NewGuid());

        config.SetActivePixProvider(PaymentProvider.SANDBOX);

        config.ActivePixProvider.Should().Be(PaymentProvider.SANDBOX);
    }

    [Fact]
    public void SetActiveCreditProvider_ShouldAllowSandbox()
    {
        var config = TenantConfig.Create(Guid.NewGuid());

        config.SetActiveCreditProvider(PaymentProvider.SANDBOX);

        config.ActiveCreditProvider.Should().Be(PaymentProvider.SANDBOX);
    }
}
