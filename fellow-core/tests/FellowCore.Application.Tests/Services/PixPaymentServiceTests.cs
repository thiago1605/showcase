using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.PixPayments.DTOs;
using FellowCore.Application.Modules.PixPayments.Services;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Models;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class PixPaymentServiceTests
{
    private readonly IPixPaymentRepository _pixPaymentRepository = Substitute.For<IPixPaymentRepository>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IOpenPixApiClient _openPixApi = Substitute.For<IOpenPixApiClient>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly ILogger<PixPaymentService> _logger = Substitute.For<ILogger<PixPaymentService>>();
    private readonly PixPaymentService _sut;

    private static readonly Guid TenantId = Guid.NewGuid();

    public PixPaymentServiceTests()
    {
        _configuration["OpenPix:AppId"].Returns("test-app-id");
        _sut = new PixPaymentService(_pixPaymentRepository, _tenantRepository, _openPixApi, _configuration, _logger);
    }

    #region CreateAsync

    [Fact]
    public async Task CreateAsync_ShouldThrowNotFoundException_WhenTenantNotFound()
    {
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns((Tenant?)null);

        var request = new CreatePixPaymentDto("chave@pix.com", 100m, "Pagamento teste");
        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Tenant*");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowBusinessException_WhenTenantHasNoConfig()
    {
        var tenant = Tenant.Create("Test", "test", "hash", "pk_test", "secrethash");
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        var request = new CreatePixPaymentDto("chave@pix.com", 100m, "Pagamento teste");
        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*configuracao*");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowConfigurationException_WhenAppIdNotConfigured()
    {
        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        // Override to return null AppId
        var configWithoutAppId = Substitute.For<IConfiguration>();
        configWithoutAppId["OpenPix:AppId"].Returns((string?)null);

        var sut = new PixPaymentService(_pixPaymentRepository, _tenantRepository, _openPixApi, configWithoutAppId, _logger);

        var request = new CreatePixPaymentDto("chave@pix.com", 100m, "Pagamento teste");
        var act = () => sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<ConfigurationException>()
            .WithMessage("*AppId*");
    }

    [Fact]
    public async Task CreateAsync_ShouldCreatePixPayment_WhenAllInputsValid()
    {
        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        _openPixApi.CreatePaymentAsync("test-app-id", Arg.Any<OpenPixPaymentRequest>())
            .Returns(new OpenPixPaymentResponse(
                new OpenPixPaymentData(10000, "CREATED", "chave@pix.com", TransactionId: "prov_tx_123")));

        var request = new CreatePixPaymentDto("chave@pix.com", 100m, "Pagamento teste");
        var result = await _sut.CreateAsync(TenantId, request);

        result.Should().NotBeNull();
        result.DestinationPixKey.Should().Be("chave@pix.com");
        result.Amount.Should().Be(100m);
        result.Status.Should().Be("PROCESSING");
        result.CorrelationId.Should().NotBeNullOrEmpty();

        _pixPaymentRepository.Received(1).Add(Arg.Any<PixPayment>());
        await _pixPaymentRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task CreateAsync_ShouldPassCorrectCentsToProvider()
    {
        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        OpenPixPaymentRequest? capturedRequest = null;
        _openPixApi.CreatePaymentAsync("test-app-id", Arg.Do<OpenPixPaymentRequest>(r => capturedRequest = r))
            .Returns(new OpenPixPaymentResponse(
                new OpenPixPaymentData(25050, "CREATED", "key@pix.com", TransactionId: "tx_001")));

        var request = new CreatePixPaymentDto("key@pix.com", 250.50m, "Pagamento parcial");
        await _sut.CreateAsync(TenantId, request);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Value.Should().Be(25050);
        capturedRequest.DestinationAlias.Should().Be("key@pix.com");
    }

    [Fact]
    public async Task CreateAsync_ShouldPropagateProviderException()
    {
        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(TenantId).Returns(tenant);

        _openPixApi.CreatePaymentAsync(Arg.Any<string>(), Arg.Any<OpenPixPaymentRequest>())
            .ThrowsAsync(new HttpRequestException("Provider unavailable"));

        var request = new CreatePixPaymentDto("chave@pix.com", 100m);
        var act = () => _sut.CreateAsync(TenantId, request);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    #endregion

    #region GetByIdAsync

    [Fact]
    public async Task GetByIdAsync_ShouldThrowNotFoundException_WhenNotFound()
    {
        _pixPaymentRepository.GetByIdAsync(TenantId, Arg.Any<Guid>()).Returns((PixPayment?)null);

        var act = () => _sut.GetByIdAsync(TenantId, Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Pagamento Pix*");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnDetail_WhenFound()
    {
        var pixPayment = PixPayment.Create(TenantId, "key@pix.com", 200m, PaymentProvider.OPENPIX, "Test payment");
        _pixPaymentRepository.GetByIdAsync(TenantId, pixPayment.Id).Returns(pixPayment);

        var result = await _sut.GetByIdAsync(TenantId, pixPayment.Id);

        result.Should().NotBeNull();
        result.Id.Should().Be(pixPayment.Id);
        result.DestinationPixKey.Should().Be("key@pix.com");
        result.Amount.Should().Be(200m);
        result.Status.Should().Be("PENDING");
    }

    #endregion

    #region ListAsync

    [Fact]
    public async Task ListAsync_ShouldReturnPagedResult()
    {
        var pix1 = PixPayment.Create(TenantId, "a@pix.com", 100m, PaymentProvider.OPENPIX);
        var pix2 = PixPayment.Create(TenantId, "b@pix.com", 200m, PaymentProvider.OPENPIX);

        _pixPaymentRepository.GetPagedAsync(TenantId, 0, 20)
            .Returns((new List<PixPayment> { pix1, pix2 }.AsEnumerable(), 2));

        var result = await _sut.ListAsync(TenantId, 1, 20);

        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnEmpty_WhenNoPayments()
    {
        _pixPaymentRepository.GetPagedAsync(TenantId, 0, 20)
            .Returns((Enumerable.Empty<PixPayment>(), 0));

        var result = await _sut.ListAsync(TenantId, 1, 20);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_ShouldNormalizePagination()
    {
        _pixPaymentRepository.GetPagedAsync(TenantId, Arg.Any<int>(), Arg.Any<int>())
            .Returns((Enumerable.Empty<PixPayment>(), 0));

        // Page 0 should normalize to 1 (skip=0), pageSize 200 should clamp to 100
        var result = await _sut.ListAsync(TenantId, 0, 200);

        result.Page.Should().Be(1);
        result.PageSize.Should().Be(100);
    }

    #endregion

    #region Tenant Isolation

    [Fact]
    public async Task GetByIdAsync_ShouldUseTenantId_ForIsolation()
    {
        var otherTenantId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        _pixPaymentRepository.GetByIdAsync(otherTenantId, paymentId).Returns((PixPayment?)null);

        var act = () => _sut.GetByIdAsync(otherTenantId, paymentId);

        await act.Should().ThrowAsync<NotFoundException>();
        await _pixPaymentRepository.Received(1).GetByIdAsync(otherTenantId, paymentId);
    }

    #endregion

    #region Helpers

    private static Tenant BuildTenantWithConfig()
    {
        var tenant = Tenant.Create("Test Tenant", "test-tenant", "testhash", "pk_test_xxxx", "hash");
        tenant.CreateDefaultConfig();
        return tenant;
    }

    #endregion
}
