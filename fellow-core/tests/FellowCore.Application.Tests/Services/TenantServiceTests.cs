using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Common.Utils;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Tenants.DTOs;
using FellowCore.Application.Modules.Tenants.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Caching.Distributed;

namespace FellowCore.Application.Tests.Services;

public class TenantServiceTests
{
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IDistributedCache _cache = Substitute.For<IDistributedCache>();
    private readonly TenantService _sut;

    public TenantServiceTests()
    {
        _sut = new TenantService(_tenantRepository, _cache, isProduction: false);
    }

    // --- CreateAsync ---

    [Fact]
    public async Task CreateAsync_ValidInput_CreatesTenantWithHashedApiKey()
    {
        var dto = new CreateTenantDto { Name = "Acme Inc", Slug = "acme" };
        _tenantRepository.GetBySlugAsync("acme").Returns((Tenant?)null);
        _tenantRepository.AddAsync(Arg.Any<Tenant>())
            .Returns(callInfo => callInfo.Arg<Tenant>());

        var result = await _sut.CreateAsync(dto);

        result.Should().NotBeNull();
        result.ApiKey.Should().StartWith("pk_test_");
        result.ApiSecret.Should().StartWith("sk_test_");
        result.Tenant.Name.Should().Be("Acme Inc");
        result.Tenant.Slug.Should().Be("acme");
        result.Tenant.MaskedApiKey.Should().Contain("****");
        await _tenantRepository.Received(1).AddAsync(Arg.Any<Tenant>());
    }

    [Fact]
    public async Task CreateAsync_DuplicateSlug_ThrowsConflictException()
    {
        var existing = Tenant.Create("Existing", "acme", "hash", "pk_test_xxxx", "hash");
        _tenantRepository.GetBySlugAsync("acme").Returns(existing);

        var dto = new CreateTenantDto { Name = "New Acme", Slug = "acme" };
        var act = () => _sut.CreateAsync(dto);

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage("*slug*");
    }

    [Fact]
    public async Task CreateAsync_ProductionMode_UsesLivePrefix()
    {
        var prodSut = new TenantService(_tenantRepository, _cache, isProduction: true);
        var dto = new CreateTenantDto { Name = "Prod Co", Slug = "prod-co" };
        _tenantRepository.GetBySlugAsync("prod-co").Returns((Tenant?)null);
        _tenantRepository.AddAsync(Arg.Any<Tenant>())
            .Returns(callInfo => callInfo.Arg<Tenant>());

        var result = await prodSut.CreateAsync(dto);

        result.ApiKey.Should().StartWith("pk_live_");
        result.ApiSecret.Should().StartWith("sk_live_");
    }

    // --- RotateApiKeyAsync ---

    [Fact]
    public async Task RotateApiKeyAsync_ValidSecret_GeneratesNewKeyAndHash()
    {
        var tenant = Tenant.Create("Test", "test", "hash", "pk_test_xxxx", "hash");
        tenant.CreateDefaultConfig();

        // We need to know the actual secret hash to validate. Create via CreateAsync first.
        var dto = new CreateTenantDto { Name = "Rotate Test", Slug = "rotate-test" };
        _tenantRepository.GetBySlugAsync("rotate-test").Returns((Tenant?)null);
        _tenantRepository.AddAsync(Arg.Any<Tenant>())
            .Returns(callInfo => callInfo.Arg<Tenant>());
        var createResult = await _sut.CreateAsync(dto);

        // Now set up the tenant returned by GetByIdWithConfigAsync to match the created tenant
        _tenantRepository.GetByIdWithConfigAsync(createResult.Tenant.Id)
            .Returns(callInfo =>
            {
                // Build a new tenant with the hash from the creation
                var t = Tenant.Create(
                    "Rotate Test", "rotate-test",
                    CryptoUtils.GenerateSha256Hash(createResult.ApiKey),
                    createResult.ApiKey[..12],
                    CryptoUtils.GenerateSha256Hash(createResult.ApiSecret));
                // We need the same Id — use reflection
                typeof(Tenant).BaseType!.BaseType!.GetProperty("Id")!.SetValue(t, createResult.Tenant.Id);
                return t;
            });

        var rotateResult = await _sut.RotateApiKeyAsync(createResult.Tenant.Id, createResult.ApiSecret);

        rotateResult.Should().NotBeNull();
        rotateResult.ApiKey.Should().StartWith("pk_test_");
        rotateResult.ApiSecret.Should().StartWith("sk_test_");
        rotateResult.ApiKey.Should().NotBe(createResult.ApiKey);
        rotateResult.ApiSecret.Should().NotBe(createResult.ApiSecret);
        await _tenantRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task RotateApiKeyAsync_InvalidSecret_ThrowsUnauthorized()
    {
        var tenant = Tenant.Create("Test", "test",
            CryptoUtils.GenerateSha256Hash("pk_test_abc"),
            "pk_test_xxxx",
            CryptoUtils.GenerateSha256Hash("sk_test_real_secret"));
        _tenantRepository.GetByIdWithConfigAsync(tenant.Id).Returns(tenant);

        var act = () => _sut.RotateApiKeyAsync(tenant.Id, "sk_test_wrong_secret");

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("*invalido*");
    }

    [Fact]
    public async Task RotateApiKeyAsync_TenantNotFound_ThrowsNotFoundException()
    {
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns((Tenant?)null);

        var act = () => _sut.RotateApiKeyAsync(Guid.NewGuid(), "any-secret");

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Tenant*");
    }

    // --- GetByIdAsync ---

    [Fact]
    public async Task GetByIdAsync_ExistingTenant_ReturnsTenant()
    {
        var tenant = Tenant.Create("Test Tenant", "test-tenant", "hash", "pk_test_xxxx", "hash");
        _tenantRepository.GetByIdWithConfigAsync(tenant.Id).Returns(tenant);

        var result = await _sut.GetByIdAsync(tenant.Id);

        result.Should().NotBeNull();
        result.Name.Should().Be("Test Tenant");
        result.Slug.Should().Be("test-tenant");
        result.MaskedApiKey.Should().Contain("****");
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ThrowsNotFoundException()
    {
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns((Tenant?)null);

        var act = () => _sut.GetByIdAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Tenant*");
    }

    // --- UpdateProvidersAsync ---

    [Fact]
    public async Task UpdateProvidersAsync_ValidInput_UpdatesConfig()
    {
        var tenant = Tenant.Create("Test", "test", "hash", "pk_test_xxxx", "hash");
        tenant.CreateDefaultConfig();
        _tenantRepository.GetByIdWithConfigAsync(tenant.Id).Returns(tenant);

        var dto = new UpdateTenantProvidersDto
        {
            ActivePixProvider = PaymentProvider.STRIPE,
            ActiveCreditProvider = PaymentProvider.STRIPE
        };

        await _sut.UpdateProvidersAsync(tenant.Id, dto);

        tenant.Config!.ActivePixProvider.Should().Be(PaymentProvider.STRIPE);
        tenant.Config!.ActiveCreditProvider.Should().Be(PaymentProvider.STRIPE);
        await _tenantRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task UpdateProvidersAsync_TenantWithNoConfig_ThrowsBusinessException()
    {
        var tenant = Tenant.Create("Test", "test", "hash", "pk_test_xxxx", "hash");
        // No config
        _tenantRepository.GetByIdWithConfigAsync(tenant.Id).Returns(tenant);

        var dto = new UpdateTenantProvidersDto { ActivePixProvider = PaymentProvider.STRIPE };
        var act = () => _sut.UpdateProvidersAsync(tenant.Id, dto);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*configura*");
    }
}
