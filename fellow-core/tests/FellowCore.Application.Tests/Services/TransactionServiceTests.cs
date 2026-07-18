using FluentAssertions;
using NSubstitute;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Rails;
using FellowCore.Application.Modules.Transactions.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class TransactionServiceTests
{
    private readonly ITenantRepository _tenantRepository = Substitute.For<ITenantRepository>();
    private readonly ISellerRepository _sellerRepository = Substitute.For<ISellerRepository>();
    private readonly ITransactionRepository _transactionRepository = Substitute.For<ITransactionRepository>();
    private readonly IPaymentIntentRepository _paymentIntentRepository = Substitute.For<IPaymentIntentRepository>();
    private readonly IRefundIntentRepository _refundIntentRepository = Substitute.For<IRefundIntentRepository>();
    private readonly ITransactionInstallmentRepository _installmentRepository = Substitute.For<ITransactionInstallmentRepository>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ILogger<TransactionService> _logger = Substitute.For<ILogger<TransactionService>>();
    private readonly TransactionService _sut;

    private static readonly PayerDto DefaultPayer = new("João Silva", "52998224725", "joao@email.com");

    public TransactionServiceTests()
    {
        var providerFactory = Substitute.For<IPaymentProviderFactory>();
        var paymentProvider = Substitute.For<IPaymentProvider>();
        paymentProvider.ProcessPaymentAsync(
                Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<CreateTransactionDto>(), Arg.Any<decimal>(), Arg.Any<string?>())
            .Returns(new GatewayPaymentDetails("prov_tx_001"));
        paymentProvider.RefundAsync(
                Arg.Any<Tenant>(), Arg.Any<Seller?>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("refund_001");

        providerFactory.GetProvider(Arg.Any<PaymentProvider>()).Returns(paymentProvider);

        var openPixApi = Substitute.For<IOpenPixApiClient>();
        var config = Substitute.For<IConfiguration>();
        var ledgerService = _ledgerService;
        var pricingService = Substitute.For<IPricingService>();
        var providerCostService = Substitute.For<IProviderCostService>();

        pricingService.CalculatePlatformFeeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<PaymentType>(), Arg.Any<int>(), Arg.Any<decimal>())
            .Returns(new PlatformFeeResult(1.50m, 98.50m));
        providerCostService.CalculateProviderCostAsync(Arg.Any<PaymentProvider>(), Arg.Any<PaymentType>(), Arg.Any<decimal>())
            .Returns(0.80m);

        // Register OpenPixRail + StripeCardRail — cast (PaymentType)99 will be unsupported for testing
        var railRouter = new RailRouter([new OpenPixRail(providerFactory), new StripeCardRail(providerFactory)]);
        var splitRuleRepository = Substitute.For<ISplitRuleRepository>();

        _sut = new TransactionService(
            _tenantRepository, _sellerRepository, _transactionRepository,
            Substitute.For<ITransactionItemRepository>(), _paymentIntentRepository,
            _refundIntentRepository, _installmentRepository,
            splitRuleRepository,
            _unitOfWork, railRouter, TimeProvider.System, openPixApi, ledgerService,
            pricingService, providerCostService, Substitute.For<IAppMetrics>(), config, _logger);
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowNotFoundException_WhenTenantNotFound()
    {
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns((Tenant?)null);

        var act = () => _sut.CreateAsync(Guid.NewGuid(), PixRequest());

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Tenant*");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowBusinessException_WhenTenantHasNoConfig()
    {
        var tenant = Tenant.Create("Test", "test", "testhash", "pk_test_xxxx", "hash");
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(tenant);

        var act = () => _sut.CreateAsync(Guid.NewGuid(), PixRequest());

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*configuração*");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowNotFoundException_WhenSellerNotFound()
    {
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(BuildTenantWithConfig());
        _sellerRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((Seller?)null);

        var act = () => _sut.CreateAsync(Guid.NewGuid(), PixRequest(sellerId: Guid.NewGuid()));

        await act.Should().ThrowAsync<NotFoundException>()
            .WithMessage("*Seller*");
    }

    [Fact]
    public async Task CreateAsync_ShouldThrow_WhenPaymentTypeNotSupported()
    {
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(BuildTenantWithConfig());

        // (PaymentType)99 is not supported by any registered rail
        var act = () => _sut.CreateAsync(Guid.NewGuid(), new CreateTransactionDto(
            SellerId: null, Amount: 100m, PaymentType: (PaymentType)99,
            Installments: 1, Description: "Teste", Payer: DefaultPayer));

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*No rail supports*");
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnTransactionResponse_WhenSuccessful()
    {
        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(tenant);

        var result = await _sut.CreateAsync(tenant.Id, PixRequest());

        result.Should().NotBeNull();
        result.Status.Should().Be(TransactionStatus.PROCESSING);
        result.Amount.Should().Be(100m);
        _transactionRepository.Received(1).Add(Arg.Any<Transaction>());
        await _unitOfWork.Received(1).CommitAsync();
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectOrder_WhenPaymentIntentAlreadyCaptured()
    {
        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(tenant);

        var capturedIntent = PaymentIntent.Create(tenant.Id, "order-123", 100m);
        capturedIntent.TryCapture(Guid.NewGuid()); // simulate prior capture

        _paymentIntentRepository.GetByExternalReferenceAsync(tenant.Id, "order-123")
            .Returns(capturedIntent);

        var act = () => _sut.CreateAsync(tenant.Id, PixRequest(externalReferenceId: "order-123"));

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*already has a captured payment*");
    }

    [Fact]
    public async Task CreateAsync_ShouldCreatePaymentIntent_WhenExternalReferenceProvided()
    {
        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(tenant);
        _paymentIntentRepository.GetByExternalReferenceAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns((PaymentIntent?)null);

        var result = await _sut.CreateAsync(tenant.Id, PixRequest(externalReferenceId: "new-order-456"));

        result.Should().NotBeNull();
        _paymentIntentRepository.Received(1).Add(Arg.Any<PaymentIntent>());
    }

    [Fact]
    public async Task CreateAsync_ShouldReuseExistingIntent_WhenNotYetCaptured()
    {
        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(tenant);

        var pendingIntent = PaymentIntent.Create(tenant.Id, "order-789", 100m);
        _paymentIntentRepository.GetByExternalReferenceAsync(tenant.Id, "order-789")
            .Returns(pendingIntent);

        var result = await _sut.CreateAsync(tenant.Id, PixRequest(externalReferenceId: "order-789"));

        result.Should().NotBeNull();
        // Should NOT add a new intent — reuses the existing one
        _paymentIntentRepository.DidNotReceive().Add(Arg.Any<PaymentIntent>());
    }

    private static CreateTransactionDto PixRequest(Guid? sellerId = null, string? externalReferenceId = null) =>
        new(SellerId: sellerId, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Teste", Payer: DefaultPayer,
            ExternalReferenceId: externalReferenceId);

    [Fact]
    public async Task RefundAsync_ShouldPersistRefundIntent_OnSuccess()
    {
        var tenant = BuildTenantWithConfig();
        var tenantId = tenant.Id;

        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 100m, paymentType: PaymentType.PIX,
            provider: PaymentProvider.OPENPIX, installments: 1, feeAmount: 2m, netAmount: 98m,
            expectedSettlementDate: null, providerTxId: "prov_refund_001");
        var transaction = txResult.Value;
        transaction.UpdateStatus(TransactionStatus.CAPTURED);

        _transactionRepository.GetByIdWithTimelineAsync(tenantId, transaction.Id).Returns(transaction);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        // Captura a refundIntent que foi adicionada pra verificar o estado final.
        // Como o serviço muta a mesma instância (.Complete()), a referência capturada
        // reflete o estado pós-Complete sem precisar de Received().Update().
        RefundIntent? captured = null;
        _refundIntentRepository.When(r => r.Add(Arg.Any<RefundIntent>()))
            .Do(call => captured = call.Arg<RefundIntent>());

        await _sut.RefundAsync(tenantId, transaction.Id, new RefundRequestDto(Amount: 50m, Reason: "test"));

        // RefundIntent é adicionado, mutado pra COMPLETED, e persistido duas vezes
        // (Add inicial + Complete). Não usamos Received().Update() — entities
        // tracked são auto-detectadas pelo ChangeTracker.
        _refundIntentRepository.Received(1).Add(Arg.Any<RefundIntent>());
        captured.Should().NotBeNull();
        captured!.Status.Should().Be(RefundIntentStatus.COMPLETED);
        await _refundIntentRepository.Received(2).SaveChangesAsync(); // once for Add, once for Complete
    }

    [Fact]
    public async Task RefundAsync_FullRefund_ShouldPersistPaymentIntentAsRefunded()
    {
        var tenant = BuildTenantWithConfig();
        var tenantId = tenant.Id;

        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 100m, paymentType: PaymentType.PIX,
            provider: PaymentProvider.OPENPIX, installments: 1, feeAmount: 2m, netAmount: 98m,
            expectedSettlementDate: null, providerTxId: "prov_full_refund");
        var transaction = txResult.Value;
        transaction.UpdateStatus(TransactionStatus.CAPTURED);

        var intent = PaymentIntent.Create(tenantId, "order-full", 100m);
        intent.TryCapture(transaction.Id);
        transaction.SetPaymentIntentId(intent.Id);

        _transactionRepository.GetByIdWithTimelineAsync(tenantId, transaction.Id).Returns(transaction);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);
        _paymentIntentRepository.GetByIdAsync(Arg.Any<Guid>(), intent.Id).Returns(intent);

        await _sut.RefundAsync(tenantId, transaction.Id, new RefundRequestDto(Amount: 100m, Reason: "full"));

        // `intent` é a mesma referência retornada pelo mock GetByIdAsync — após o
        // service chamar MarkRefunded(), checamos o estado diretamente. SaveChangesAsync
        // é o suficiente pra confirmar a persistência (sem precisar Received().Update()).
        intent.Status.Should().Be(PaymentIntentStatus.REFUNDED);
        await _paymentIntentRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task RefundAsync_FullRefund_AdvanceMode_ReversesAdvanceFee()
    {
        // ADVANCE TX refunded → fee de antecipação tem que voltar pra evitar double-charge.
        var tenant = BuildTenantWithConfig();
        var tenantId = tenant.Id;

        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 600m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 6, feeAmount: 30m, netAmount: 570m,
            expectedSettlementDate: null, providerTxId: "prov_advance_refund", sellerId: Guid.NewGuid());
        var transaction = txResult.Value;
        transaction.UpdateStatus(TransactionStatus.CAPTURED);
        transaction.MarkAsAdvanceSettlement(19.95m); // fee = 3.5% × 570

        _transactionRepository.GetByIdWithTimelineAsync(tenantId, transaction.Id).Returns(transaction);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        await _sut.RefundAsync(tenantId, transaction.Id, new RefundRequestDto(Amount: 600m, Reason: "advance full refund"));

        // Hooks: cancela parcelas E reverte advance fee
        await _installmentRepository.Received(1)
            .CancelPendingForTransactionAsync(transaction.Id, Arg.Any<DateTime>());
        await _ledgerService.Received(1).ReverseAdvanceFeeAsync(
            tenantId, transaction.SellerId!.Value, 19.95m,
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RefundAsync_FullRefund_InstallmentMode_DoesNotReverseAdvanceFee()
    {
        // INSTALLMENT TX refunded — não tem advance fee pra reverter.
        var tenant = BuildTenantWithConfig();
        var tenantId = tenant.Id;
        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 600m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 6, feeAmount: 30m, netAmount: 570m,
            expectedSettlementDate: null, providerTxId: "prov_inst_refund");
        var transaction = txResult.Value;
        transaction.UpdateStatus(TransactionStatus.CAPTURED);
        // NOT calling MarkAsAdvanceSettlement → permanece INSTALLMENT
        _transactionRepository.GetByIdWithTimelineAsync(tenantId, transaction.Id).Returns(transaction);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        await _sut.RefundAsync(tenantId, transaction.Id, new RefundRequestDto(Amount: 600m, Reason: "full refund"));

        await _ledgerService.DidNotReceive().ReverseAdvanceFeeAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RefundAsync_FullRefund_CancelsPendingInstallments()
    {
        // Refund total deve cancelar parcelas PENDING pra elas não virarem
        // settlement futuro. Parcelas SETTLED não são tocadas (dinheiro já
        // foi pro WALLET e foi debitado via ledger no refund).
        var tenant = BuildTenantWithConfig();
        var tenantId = tenant.Id;

        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 600m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 6, feeAmount: 30m, netAmount: 570m,
            expectedSettlementDate: null, providerTxId: "prov_full_refund_credit");
        var transaction = txResult.Value;
        transaction.UpdateStatus(TransactionStatus.CAPTURED);

        _transactionRepository.GetByIdWithTimelineAsync(tenantId, transaction.Id).Returns(transaction);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        await _sut.RefundAsync(tenantId, transaction.Id, new RefundRequestDto(Amount: 600m, Reason: "full"));

        // TX vira REFUNDED → service chama CancelPending
        await _installmentRepository.Received(1)
            .CancelPendingForTransactionAsync(transaction.Id, Arg.Any<DateTime>());
    }

    [Fact]
    public async Task RefundAsync_PartialRefund_DoesNotCancelInstallments()
    {
        // Refund parcial: o débito do ledger já cobre. Parcelas pendentes continuam
        // PENDING porque vão liberar e o seller já foi debitado proporcionalmente.
        // Cancelar gerar drift (seller deveria receber as parcelas restantes).
        var tenant = BuildTenantWithConfig();
        var tenantId = tenant.Id;

        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 600m, paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE, installments: 6, feeAmount: 30m, netAmount: 570m,
            expectedSettlementDate: null, providerTxId: "prov_partial_refund_credit");
        var transaction = txResult.Value;
        transaction.UpdateStatus(TransactionStatus.CAPTURED);

        _transactionRepository.GetByIdWithTimelineAsync(tenantId, transaction.Id).Returns(transaction);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);

        await _sut.RefundAsync(tenantId, transaction.Id, new RefundRequestDto(Amount: 100m, Reason: "partial"));

        // TX permanece CAPTURED (refund parcial) → não cancela parcelas
        await _installmentRepository.DidNotReceive()
            .CancelPendingForTransactionAsync(Arg.Any<Guid>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task RefundAsync_PartialRefund_ShouldPersistPaymentIntentAsPartiallyRefunded()
    {
        var tenant = BuildTenantWithConfig();
        var tenantId = tenant.Id;

        var txResult = Transaction.Create(
            tenantId: tenantId, amount: 100m, paymentType: PaymentType.PIX,
            provider: PaymentProvider.OPENPIX, installments: 1, feeAmount: 2m, netAmount: 98m,
            expectedSettlementDate: null, providerTxId: "prov_partial_refund");
        var transaction = txResult.Value;
        transaction.UpdateStatus(TransactionStatus.CAPTURED);

        var intent = PaymentIntent.Create(tenantId, "order-partial", 100m);
        intent.TryCapture(transaction.Id);
        transaction.SetPaymentIntentId(intent.Id);

        _transactionRepository.GetByIdWithTimelineAsync(tenantId, transaction.Id).Returns(transaction);
        _tenantRepository.GetByIdWithConfigAsync(tenantId).Returns(tenant);
        _paymentIntentRepository.GetByIdAsync(Arg.Any<Guid>(), intent.Id).Returns(intent);

        await _sut.RefundAsync(tenantId, transaction.Id, new RefundRequestDto(Amount: 30m, Reason: "partial"));

        intent.Status.Should().Be(PaymentIntentStatus.PARTIALLY_REFUNDED);
        await _paymentIntentRepository.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task CreateAsync_ShouldThrow_WhenStripeCardSplitsWithDirectChargeMode()
    {
        var tenant = BuildTenantWithConfig(StripeChargeMode.DIRECT_CHARGE);
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(tenant);

        var splits = new List<SplitDto>
        {
            new(SellerId: Guid.NewGuid(), Amount: 50m),
            new(SellerId: Guid.NewGuid(), Amount: 30m)
        };

        var request = new CreateTransactionDto(
            SellerId: null, Amount: 100m, PaymentType: PaymentType.CREDIT_CARD,
            Installments: 1, Description: "Teste", Payer: DefaultPayer,
            Splits: splits);

        var act = () => _sut.CreateAsync(tenant.Id, request);

        await act.Should().ThrowAsync<BusinessException>()
            .WithMessage("*Direct Charge*");
    }

    [Fact]
    public async Task CreateAsync_ShouldAllow_WhenOpenPixPixSplitsAndStripeDirectChargeMode()
    {
        var tenant = BuildTenantWithConfig(StripeChargeMode.DIRECT_CHARGE);
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(tenant);

        var splits = new List<SplitDto>
        {
            new(SellerId: Guid.NewGuid(), Amount: 50m),
        };

        var request = new CreateTransactionDto(
            SellerId: null, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Teste", Payer: DefaultPayer,
            Splits: splits);

        // Should NOT throw — PIX uses OpenPix, not Stripe, so Direct Charge config is irrelevant
        var act = () => _sut.CreateAsync(tenant.Id, request);

        await act.Should().NotThrowAsync<BusinessException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldAllow_WhenStripeCardSplitsWithDestinationChargeMode()
    {
        var tenant = BuildTenantWithConfig(StripeChargeMode.DESTINATION_CHARGE);
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(tenant);

        var splits = new List<SplitDto>
        {
            new(SellerId: Guid.NewGuid(), Amount: 50m),
            new(SellerId: Guid.NewGuid(), Amount: 30m)
        };

        var request = new CreateTransactionDto(
            SellerId: null, Amount: 100m, PaymentType: PaymentType.CREDIT_CARD,
            Installments: 1, Description: "Teste", Payer: DefaultPayer,
            Splits: splits);

        var act = () => _sut.CreateAsync(tenant.Id, request);

        await act.Should().NotThrowAsync<BusinessException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldPersistFeeAllocationPolicy()
    {
        var tenant = BuildTenantWithConfig();
        _tenantRepository.GetByIdWithConfigAsync(Arg.Any<Guid>()).Returns(tenant);

        Transaction? capturedTx = null;
        _transactionRepository.When(r => r.Add(Arg.Any<Transaction>()))
            .Do(ci => capturedTx = ci.Arg<Transaction>());

        var request = new CreateTransactionDto(
            SellerId: null, Amount: 100m, PaymentType: PaymentType.PIX,
            Installments: 1, Description: "Teste", Payer: DefaultPayer,
            FeeAllocationPolicy: FeeAllocationPolicy.PROPORTIONAL_TO_RECIPIENTS);

        await _sut.CreateAsync(tenant.Id, request);

        capturedTx.Should().NotBeNull();
        capturedTx!.FeeAllocationPolicy.Should().Be(FeeAllocationPolicy.PROPORTIONAL_TO_RECIPIENTS);
    }

    private static Tenant BuildTenantWithConfig(StripeChargeMode chargeMode = StripeChargeMode.DESTINATION_CHARGE)
    {
        var tenant = Tenant.Create("Test Tenant", "test-tenant", "testhash", "pk_test_xxxx", "hash");
        tenant.CreateDefaultConfig();
        tenant.Config!.SetStripeChargeMode(chargeMode);
        return tenant;
    }
}
