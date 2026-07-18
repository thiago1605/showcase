using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.DTOs;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Sellers.DTOs;
using FellowCore.Application.Modules.Sellers.Services;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class SellerServiceTests
{
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly IPaymentProviderFactory _providerFactory = Substitute.For<IPaymentProviderFactory>();
    private readonly IOpenPixApiClient _openPixApi = Substitute.For<IOpenPixApiClient>();
    private readonly IStripeApiClient _stripeApi = Substitute.For<IStripeApiClient>();
    private readonly ISecurityService _securityService = Substitute.For<ISecurityService>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly ITransactionInstallmentRepository _installmentRepository = Substitute.For<ITransactionInstallmentRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IConfiguration _configuration = Substitute.For<IConfiguration>();
    private readonly SellerService _sut;

    public SellerServiceTests()
    {
        _securityService.EncryptAsync(Arg.Any<string>()).Returns("encrypted-value");
        // Default: nenhuma parcela pendente — testes que precisam de release schedule sobrescrevem.
        _installmentRepository.GetReleaseScheduleAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<InstallmentReleaseSlot>());

        _sut = new SellerService(
            _sellerRepository, _tenantRepository, _providerFactory,
            _openPixApi, _stripeApi, _securityService, _ledgerService,
            _installmentRepository,
            _configuration, _unitOfWork,
            Substitute.For<ILogger<SellerService>>());
    }

    // --- CreateAsync ---

    [Fact]
    public async Task CreateAsync_ValidInput_CreatesSeller()
    {
        var tenantId = Guid.NewGuid();
        var tenant = BuildTenantWithConfig();
        var request = BuildCreateSellerDto();

        _sellerRepository.ExistsByDocumentAsync(tenantId, request.Document).Returns(false);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        var provider = Substitute.For<IPaymentProvider>();
        provider.CreateSubAccountAsync(tenant, request)
            .Returns(new GatewaySubAccountDetails("ext-123", "api-key-001", "pix-key-001"));
        _providerFactory.GetProvider(PaymentProvider.OPENPIX).Returns(provider);

        var result = await _sut.CreateAsync(tenantId, request);

        result.Should().NotBeNull();
        result.LegalName.Should().Be(request.LegalName);
        result.Document.Should().Be(request.Document);
        result.Status.Should().Be(SellerStatus.ACTIVE);
        _sellerRepository.Received(1).Add(Arg.Any<Seller>());
        await _unitOfWork.Received(1).CommitAsync();
    }

    [Fact]
    public async Task CreateAsync_DuplicateDocument_ThrowsConflict()
    {
        var tenantId = Guid.NewGuid();
        _sellerRepository.ExistsByDocumentAsync(tenantId, Arg.Any<string>()).Returns(true);

        var act = () => _sut.CreateAsync(tenantId, BuildCreateSellerDto());

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage("*documento*");
    }

    [Fact]
    public async Task CreateAsync_TenantNotFound_ThrowsNotFoundException()
    {
        var tenantId = Guid.NewGuid();
        _sellerRepository.ExistsByDocumentAsync(tenantId, Arg.Any<string>()).Returns(false);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns((Tenant?)null);

        var act = () => _sut.CreateAsync(tenantId, BuildCreateSellerDto());

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Tenant*");
    }

    [Fact]
    public async Task CreateAsync_TenantHasNoConfig_ThrowsBusinessException()
    {
        var tenantId = Guid.NewGuid();
        var tenant = Tenant.Create("Test", "test", "hash", "pk_test_xxxx", "hash");
        // No config attached

        _sellerRepository.ExistsByDocumentAsync(tenantId, Arg.Any<string>()).Returns(false);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        var act = () => _sut.CreateAsync(tenantId, BuildCreateSellerDto());

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*configura*");
    }

    // --- GetBalanceAsync ---

    [Fact]
    public async Task GetBalanceAsync_FallsBackToLedger_ReturnsLedgerBalance()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var seller = BuildSeller(tenantId, sellerId, preferredProvider: PaymentProvider.OPENPIX);
        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);

        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        // Provider throws NotSupportedException -> falls back to ledger
        var provider = Substitute.For<IPaymentProvider>();
        provider.GetAccountBalanceAsync(tenant, seller).Throws(new NotSupportedException());
        _providerFactory.GetProvider(PaymentProvider.OPENPIX).Returns(provider);

        _ledgerService.GetBalanceAsync(tenantId, sellerId)
            .Returns(new LedgerBalanceResponse(Available: 500m, WaitingFunds: 100m, Disputed: 0m, Total: 600m));

        var result = await _sut.GetBalanceAsync(tenantId, sellerId);

        result.SellerId.Should().Be(sellerId);
        result.Available.Should().Be(500m);
        result.Blocked.Should().Be(100m);
        result.Total.Should().Be(600m);
        result.IsAccountReady.Should().BeTrue();
    }

    [Fact]
    public async Task GetBalanceAsync_ProviderReturnsBalance_ReturnsProviderBalance()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        var seller = BuildSeller(tenantId, sellerId, preferredProvider: PaymentProvider.OPENPIX);
        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);

        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        var provider = Substitute.For<IPaymentProvider>();
        provider.GetAccountBalanceAsync(tenant, seller)
            .Returns(new AccountBalanceDetails(TotalInReais: 1000m, BlockedInReais: 200m, AvailableInReais: 800m, IsReady: true));
        _providerFactory.GetProvider(PaymentProvider.OPENPIX).Returns(provider);

        var result = await _sut.GetBalanceAsync(tenantId, sellerId);

        result.Total.Should().Be(1000m);
        result.Blocked.Should().Be(200m);
        result.Available.Should().Be(800m);
        result.IsAccountReady.Should().BeTrue();
    }

    [Fact]
    public async Task GetBalanceAsync_SellerNotFound_ThrowsNotFoundException()
    {
        var tenantId = Guid.NewGuid();
        _sellerRepository.GetByIdAsync(tenantId, Arg.Any<Guid>()).Returns((Seller?)null);

        var act = () => _sut.GetBalanceAsync(tenantId, Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Seller*");
    }

    [Fact]
    public async Task GetBalanceAsync_NoBlockedFunds_DoesNotQueryReleaseSchedule()
    {
        // Quando blocked=0, é desperdício rodar o GROUP BY no banco —
        // service deve curto-circuitar e retornar buckets nulos.
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var seller = BuildSeller(tenantId, sellerId, preferredProvider: PaymentProvider.OPENPIX);
        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(BuildTenantWithConfig());

        var provider = Substitute.For<IPaymentProvider>();
        provider.GetAccountBalanceAsync(Arg.Any<Tenant>(), Arg.Any<Seller>())
            .Returns(new AccountBalanceDetails(TotalInReais: 500m, BlockedInReais: 0m, AvailableInReais: 500m, IsReady: true));
        _providerFactory.GetProvider(PaymentProvider.OPENPIX).Returns(provider);

        var result = await _sut.GetBalanceAsync(tenantId, sellerId);

        result.Blocked.Should().Be(0m);
        result.BlockedByDate.Should().BeNull();
        result.BlockedBuckets.Should().BeNull();
        await _installmentRepository.DidNotReceive().GetReleaseScheduleAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<int>());
    }

    [Fact]
    public async Task GetBalanceAsync_WithBlockedFunds_PopulatesScheduleAndBuckets()
    {
        // Caso central: seller com débito (D+2), 2x crédito à vista (D+30), e crédito 3x.
        // Buckets devem ser cumulativos: Next7 contém Next2; Next30 contém Next7.
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var seller = BuildSeller(tenantId, sellerId, preferredProvider: PaymentProvider.STRIPE);
        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(BuildTenantWithConfig());

        var provider = Substitute.For<IPaymentProvider>();
        provider.GetAccountBalanceAsync(Arg.Any<Tenant>(), Arg.Any<Seller>())
            .Returns(new AccountBalanceDetails(TotalInReais: 6000m, BlockedInReais: 5500m, AvailableInReais: 500m, IsReady: true));
        _providerFactory.GetProvider(PaymentProvider.STRIPE).Returns(provider);

        var now = DateTime.UtcNow;
        // Cenário realista marketplace: débito (D+2), crédito 1x (D+30), 3x (D+90), 6x (D+180), 12x (D+360)
        _installmentRepository.GetReleaseScheduleAsync(tenantId, sellerId, Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<InstallmentReleaseSlot>
            {
                new(now.AddDays(2).Date,   2000m),
                new(now.AddDays(30).Date,  2000m),
                new(now.AddDays(90).Date,   500m),
                new(now.AddDays(180).Date,  600m),
                new(now.AddDays(360).Date,  400m),
            });

        var result = await _sut.GetBalanceAsync(tenantId, sellerId);

        result.Blocked.Should().Be(5500m);
        result.BlockedByDate.Should().HaveCount(5);
        result.BlockedBuckets.Should().NotBeNull();
        result.BlockedBuckets!.Next2Days.Should().Be(2000m, "só o slot D+2 entra em next2");
        result.BlockedBuckets.Next7Days.Should().Be(2000m, "nada entre D+3 e D+7");
        result.BlockedBuckets.Next30Days.Should().Be(4000m, "next30 inclui D+2 + D+30");
        result.BlockedBuckets.Next90Days.Should().Be(4500m, "next90 inclui +D+90");
        result.BlockedBuckets.Next180Days.Should().Be(5100m, "next180 inclui +D+180");
        result.BlockedBuckets.Next365Days.Should().Be(5500m, "next365 cobre tudo");
    }

    [Fact]
    public async Task GetBalanceAsync_EmptySchedule_ButBlockedNonZero_BucketsAreZero()
    {
        // Edge: provider reporta blocked > 0 mas não temos rows pendentes no nosso DB
        // (e.g. dinheiro veio de fora do fluxo de TX — drift entre Stripe e ledger).
        // Não devemos crashar nem mentir: buckets zero, blockedByDate vazio.
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var seller = BuildSeller(tenantId, sellerId, preferredProvider: PaymentProvider.STRIPE);
        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(BuildTenantWithConfig());

        var provider = Substitute.For<IPaymentProvider>();
        provider.GetAccountBalanceAsync(Arg.Any<Tenant>(), Arg.Any<Seller>())
            .Returns(new AccountBalanceDetails(TotalInReais: 1000m, BlockedInReais: 1000m, AvailableInReais: 0m, IsReady: true));
        _providerFactory.GetProvider(PaymentProvider.STRIPE).Returns(provider);

        // GetReleaseScheduleAsync já volta lista vazia (default no construtor)

        var result = await _sut.GetBalanceAsync(tenantId, sellerId);

        result.Blocked.Should().Be(1000m);
        result.BlockedByDate.Should().BeEmpty();
        result.BlockedBuckets!.Next2Days.Should().Be(0m);
        result.BlockedBuckets.Next7Days.Should().Be(0m);
        result.BlockedBuckets.Next30Days.Should().Be(0m);
        result.BlockedBuckets.Next90Days.Should().Be(0m);
        result.BlockedBuckets.Next180Days.Should().Be(0m);
        result.BlockedBuckets.Next365Days.Should().Be(0m);
    }

    // --- ListAsync ---

    [Fact]
    public async Task ListAsync_FiltersByTenant_ReturnsPagedResult()
    {
        var tenantId = Guid.NewGuid();
        var sellers = new List<Seller>
        {
            BuildSeller(tenantId, Guid.NewGuid()),
            BuildSeller(tenantId, Guid.NewGuid())
        };

        _sellerRepository.GetPagedAsync(tenantId, 0, 20)
            .Returns((sellers.AsReadOnly(), 2));

        var result = await _sut.ListAsync(tenantId, 1, 20);

        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(1);
    }

    // --- UpdateAsync ---

    [Fact]
    public async Task UpdateAsync_ValidInput_UpdatesSeller()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var seller = BuildSeller(tenantId, sellerId);

        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);

        var request = new UpdateSellerDto(TradeName: "New Trade Name", Email: "new@email.com");
        var result = await _sut.UpdateAsync(tenantId, sellerId, request);

        result.Should().NotBeNull();
        result.TradeName.Should().Be("New Trade Name");
        result.Email.Should().Be("new@email.com");
        _sellerRepository.Received(1).Update(seller);
        await _sellerRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task UpdateAsync_SellerNotFound_ThrowsNotFoundException()
    {
        _sellerRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((Seller?)null);

        var act = () => _sut.UpdateAsync(Guid.NewGuid(), Guid.NewGuid(), new UpdateSellerDto());

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Seller*");
    }

    // --- GetByIdAsync ---

    [Fact]
    public async Task GetByIdAsync_ReturnsSeller()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var seller = BuildSeller(tenantId, sellerId);

        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(seller);

        var result = await _sut.GetByIdAsync(tenantId, sellerId);

        result.Should().NotBeNull();
        result.Id.Should().Be(sellerId);
        result.LegalName.Should().Be("Test Seller");
    }

    // --- Helpers ---

    private static Tenant BuildTenantWithConfig()
    {
        var tenant = Tenant.Create("Test Tenant", "test-tenant", "testhash", "pk_test_xxxx", "hash");
        tenant.CreateDefaultConfig();
        return tenant;
    }

    private static CreateSellerDto BuildCreateSellerDto() => new(
        LegalName: "Test Seller",
        TradeName: "Test Trade",
        Document: "12345678901",
        Email: "seller@test.com",
        IncomeValue: 5000m,
        BirthDate: "1990-01-01",
        MobilePhone: "11999999999",
        Address: new SellerAddressDto("Rua A", "100", null, "Centro", "SP", "SP", "01000-000"),
        FeeDebit: 2m,
        FeeCreditCash: null,
        FeeCreditInstallment: null,
        FeePixIn: 1m,
        PayoutFixedFee: null,
        PayoutPercentFee: null,
        BusinessDescription: null,
        BusinessProduct: null,
        BusinessLifetime: null,
        BusinessGoal: null,
        Documents: null
    );

    private static Seller BuildSeller(Guid tenantId, Guid? sellerId = null, PaymentProvider? preferredProvider = null)
    {
        var seller = Seller.Create(
            tenantId: tenantId,
            legalName: "Test Seller",
            document: "12345678901",
            email: "seller@test.com",
            webhookSecret: "encrypted-secret",
            preferredProvider: preferredProvider,
            externalAccountId: "ext-account-123",
            encryptedAccessToken: "encrypted-token"
        );

        // Use reflection to set the Id for testing purposes
        if (sellerId.HasValue)
        {
            typeof(Seller).BaseType!.BaseType!.GetProperty("Id")!
                .SetValue(seller, sellerId.Value);
        }

        return seller;
    }
}
