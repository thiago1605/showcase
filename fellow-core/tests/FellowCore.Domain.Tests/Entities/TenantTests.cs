using FluentAssertions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Events;

namespace FellowCore.Domain.Tests.Entities;

public class TenantTests
{
    [Fact]
    public void Create_ShouldSetAllProperties()
    {
        var tenant = Tenant.Create("Loja A", "loja-a", "hash123", "fp_live_", "secret_hash");

        tenant.Id.Should().NotBeEmpty();
        tenant.Name.Should().Be("Loja A");
        tenant.Slug.Should().Be("loja-a");
        tenant.ApiKeyHash.Should().Be("hash123");
        tenant.ApiKeyPrefix.Should().Be("fp_live_");
        tenant.ApiSecretHash.Should().Be("secret_hash");
        tenant.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        tenant.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        tenant.IsDeleted.Should().BeFalse();
        tenant.DeletedAt.Should().BeNull();
    }

    [Fact]
    public void Create_ShouldRaiseTenantCreatedEvent()
    {
        var tenant = Tenant.Create("Loja B", "loja-b", "hash", "prefix", "secret", "owner@test.com");

        tenant.DomainEvents.Should().ContainSingle(e => e is TenantCreatedEvent);
        var @event = (TenantCreatedEvent)tenant.DomainEvents[0];
        @event.TenantId.Should().Be(tenant.Id);
        @event.Name.Should().Be("Loja B");
        @event.Slug.Should().Be("loja-b");
        @event.OwnerEmail.Should().Be("owner@test.com");
    }

    [Fact]
    public void Create_ShouldRaiseTenantCreatedEvent_WithNullOwnerEmail()
    {
        var tenant = Tenant.Create("Loja C", "loja-c", "hash", "prefix", "secret");

        var @event = (TenantCreatedEvent)tenant.DomainEvents[0];
        @event.OwnerEmail.Should().BeNull();
    }

    [Fact]
    public void AttachConfig_ShouldSetConfigAndUpdateTimestamp()
    {
        var tenant = Tenant.Create("Loja", "loja", "hash", "prefix", "secret");
        var config = TenantConfig.Create(tenant.Id);
        var beforeUpdate = tenant.UpdatedAt;

        tenant.AttachConfig(config);

        tenant.Config.Should().Be(config);
        tenant.UpdatedAt.Should().BeOnOrAfter(beforeUpdate);
    }

    [Fact]
    public void SoftDelete_ShouldMarkAsDeleted()
    {
        var tenant = Tenant.Create("Loja", "loja", "hash", "prefix", "secret");

        tenant.SoftDelete();

        tenant.IsDeleted.Should().BeTrue();
        tenant.DeletedAt.Should().NotBeNull();
        tenant.DeletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void RotateApiKey_ShouldUpdateKeysAndClearSecret()
    {
        var tenant = Tenant.Create("Loja", "loja", "old_hash", "old_prefix", "old_secret");
        var beforeUpdate = tenant.UpdatedAt;

        tenant.RotateApiKey("new_hash", "new_prefix", "new_secret_hash");

        tenant.ApiKeyHash.Should().Be("new_hash");
        tenant.ApiKeyPrefix.Should().Be("new_prefix");
        tenant.ApiSecretHash.Should().Be("new_secret_hash");
        tenant.ApiSecret.Should().BeNull();
        tenant.UpdatedAt.Should().BeOnOrAfter(beforeUpdate);
    }

    [Fact]
    public void ClearApiSecret_ShouldSetApiSecretToNull()
    {
        var tenant = Tenant.Create("Loja", "loja", "hash", "prefix", "secret");

        tenant.ClearApiSecret();

        tenant.ApiSecret.Should().BeNull();
    }

    [Fact]
    public void CreateDefaultConfig_ShouldCreateAndAttachConfig()
    {
        var tenant = Tenant.Create("Loja", "loja", "hash", "prefix", "secret");

        var config = tenant.CreateDefaultConfig();

        config.Should().NotBeNull();
        config.TenantId.Should().Be(tenant.Id);
        tenant.Config.Should().Be(config);
    }

    [Fact]
    public void ClearDomainEvents_ShouldEmptyEvents()
    {
        var tenant = Tenant.Create("Loja", "loja", "hash", "prefix", "secret");
        tenant.DomainEvents.Should().NotBeEmpty();

        tenant.ClearDomainEvents();

        tenant.DomainEvents.Should().BeEmpty();
    }
}
