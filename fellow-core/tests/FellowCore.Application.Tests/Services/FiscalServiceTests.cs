using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Fiscal;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Tests.Services;

public class FiscalServiceTests
{
    private readonly ISellerFiscalSettingsRepository _fiscalSettingsRepository = Substitute.For<ISellerFiscalSettingsRepository>();
    private readonly IFiscalInvoiceRepository _fiscalInvoiceRepository = Substitute.For<IFiscalInvoiceRepository>();
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FiscalService _sut;

    public FiscalServiceTests()
    {
        _sut = new FiscalService(
            _fiscalSettingsRepository,
            _fiscalInvoiceRepository,
            _transactionRepository,
            _sellerRepository,
            _unitOfWork);
    }

    [Fact]
    public async Task GetOrCreateSettingsAsync_NoExisting_CreatesNew()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        _fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId).Returns((SellerFiscalSettings?)null);
        _sellerRepository.GetByIdAsync(tenantId, sellerId).Returns(BuildSeller(tenantId, sellerId));

        var result = await _sut.GetOrCreateSettingsAsync(tenantId, sellerId);

        result.Should().NotBeNull();
        result.SellerId.Should().Be(sellerId);
        result.Enabled.Should().BeFalse();
        await _fiscalSettingsRepository.Received(1).AddAsync(Arg.Any<SellerFiscalSettings>());
    }

    [Fact]
    public async Task GetOrCreateSettingsAsync_AlreadyExists_ReturnsExisting()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var existing = SellerFiscalSettings.Create(tenantId, sellerId);

        _fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId).Returns(existing);

        var result = await _sut.GetOrCreateSettingsAsync(tenantId, sellerId);

        result.Should().Be(existing);
        await _fiscalSettingsRepository.DidNotReceive().AddAsync(Arg.Any<SellerFiscalSettings>());
    }

    [Fact]
    public async Task UpdateSettingsAsync_UpdatesFields()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var existing = SellerFiscalSettings.Create(tenantId, sellerId);

        _fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId).Returns(existing);

        var dto = new UpdateFiscalSettingsDto("123456", "1.02", 5.0m, "3550308");
        var result = await _sut.UpdateSettingsAsync(tenantId, sellerId, dto);

        result.MunicipalRegistration.Should().Be("123456");
        result.ServiceCode.Should().Be("1.02");
        result.IssRate.Should().Be(5.0m);
        result.CityCode.Should().Be("3550308");
    }

    [Fact]
    public async Task EnableAsync_WithValidConfig_Enables()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var settings = SellerFiscalSettings.Create(tenantId, sellerId, "123456", "1.02", 5.0m, "3550308");

        _fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId).Returns(settings);

        await _sut.EnableAsync(tenantId, sellerId);

        settings.Enabled.Should().BeTrue();
        await _unitOfWork.Received(1).CommitAsync();
    }

    [Fact]
    public async Task EnableAsync_MissingRegistration_Throws()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var settings = SellerFiscalSettings.Create(tenantId, sellerId);

        _fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId).Returns(settings);

        var act = () => _sut.EnableAsync(tenantId, sellerId);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*Inscricao municipal*");
    }

    [Fact]
    public async Task EnableAsync_MissingServiceCode_Throws()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var settings = SellerFiscalSettings.Create(tenantId, sellerId, "123456");

        _fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId).Returns(settings);

        var act = () => _sut.EnableAsync(tenantId, sellerId);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*Codigo de servico*");
    }

    [Fact]
    public async Task DisableAsync_Disables()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var settings = SellerFiscalSettings.Create(tenantId, sellerId, "123456", "1.02", 5.0m, "3550308");
        settings.Enable();

        _fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId).Returns(settings);

        await _sut.DisableAsync(tenantId, sellerId);

        settings.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task RequestInvoiceAsync_ValidCapturedTransaction_CreatesInvoice()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var transaction = BuildCapturedTransaction(tenantId, sellerId, transactionId, 200m);
        var settings = SellerFiscalSettings.Create(tenantId, sellerId, "123456", "1.02", 5.0m, "3550308");
        settings.Enable();

        _fiscalInvoiceRepository.GetByTransactionIdAsync(tenantId, transactionId).Returns((FiscalInvoice?)null);
        _transactionRepository.GetByIdAsync(tenantId, transactionId).Returns(transaction);
        _fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId).Returns(settings);

        var result = await _sut.RequestInvoiceAsync(tenantId, transactionId);

        result.Should().NotBeNull();
        result.TransactionId.Should().Be(transactionId);
        result.Amount.Should().Be(200m);
        result.IssAmount.Should().Be(10m); // 5% of 200
        result.Status.Should().Be(FiscalInvoiceStatus.PENDING);
    }

    [Fact]
    public async Task RequestInvoiceAsync_AlreadyExists_ReturnsExisting()
    {
        var tenantId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var existing = FiscalInvoice.Create(tenantId, Guid.NewGuid(), transactionId, 200m, 10m);

        _fiscalInvoiceRepository.GetByTransactionIdAsync(tenantId, transactionId).Returns(existing);

        var result = await _sut.RequestInvoiceAsync(tenantId, transactionId);

        result.Should().Be(existing);
        await _fiscalInvoiceRepository.DidNotReceive().AddAsync(Arg.Any<FiscalInvoice>());
    }

    [Fact]
    public async Task RequestInvoiceAsync_TransactionNotCaptured_Throws()
    {
        var tenantId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var transaction = BuildTransaction(tenantId, transactionId);

        _fiscalInvoiceRepository.GetByTransactionIdAsync(tenantId, transactionId).Returns((FiscalInvoice?)null);
        _transactionRepository.GetByIdAsync(tenantId, transactionId).Returns(transaction);

        var act = () => _sut.RequestInvoiceAsync(tenantId, transactionId);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*capturadas*");
    }

    [Fact]
    public async Task RequestInvoiceAsync_FiscalDisabled_Throws()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var transaction = BuildCapturedTransaction(tenantId, sellerId, transactionId, 200m);
        var settings = SellerFiscalSettings.Create(tenantId, sellerId, "123456", "1.02", 5.0m, "3550308");
        // settings.Enabled is false by default

        _fiscalInvoiceRepository.GetByTransactionIdAsync(tenantId, transactionId).Returns((FiscalInvoice?)null);
        _transactionRepository.GetByIdAsync(tenantId, transactionId).Returns(transaction);
        _fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId).Returns(settings);

        var act = () => _sut.RequestInvoiceAsync(tenantId, transactionId);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*desabilitada*");
    }

    private static Seller BuildSeller(Guid tenantId, Guid sellerId)
    {
        var seller = Seller.Create(
            tenantId, "Test Seller", "12345678000190", "test@test.com",
            "webhook-secret", PaymentProvider.STRIPE);
        typeof(Seller).GetProperty("Id")!.SetValue(seller, sellerId);
        return seller;
    }

    private static Transaction BuildCapturedTransaction(Guid tenantId, Guid sellerId, Guid transactionId, decimal amount)
    {
        var result = Transaction.Create(
            tenantId: tenantId,
            amount: amount,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: amount * 0.05m,
            netAmount: amount * 0.95m,
            expectedSettlementDate: null,
            providerTxId: $"prov-{transactionId}",
            sellerId: sellerId);
        var tx = result.Value;
        tx.UpdateStatus(TransactionStatus.CAPTURED);
        typeof(Transaction).GetProperty("Id")!.SetValue(tx, transactionId);
        return tx;
    }

    private static Transaction BuildTransaction(Guid tenantId, Guid transactionId)
    {
        // Default status is PROCESSING (not CAPTURED)
        var result = Transaction.Create(
            tenantId: tenantId,
            amount: 100m,
            paymentType: PaymentType.PIX,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: 5m,
            netAmount: 95m,
            expectedSettlementDate: null,
            providerTxId: $"prov-{transactionId}",
            sellerId: Guid.NewGuid());
        var tx = result.Value;
        typeof(Transaction).GetProperty("Id")!.SetValue(tx, transactionId);
        return tx;
    }
}
