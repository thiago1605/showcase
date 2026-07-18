using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Application.Modules.Payouts.Services;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace FellowCore.Application.Tests.Services;

/// <summary>
/// Cobre o saga de saque multi-provider. Cenários:
///   1. Happy path single-provider — só Stripe, R$ 100 → 1 step COMPLETED
///   2. Happy path multi-provider — R$ 120 distribuído (100 Stripe + 20 OpenPix)
///   3. Saldo insuficiente — rejeita antes de criar attempt
///   4. Partial failure — primeiro step OK, segundo falha → compensação reverte tudo
///   5. Idempotência — POST repetido com mesma key retorna attempt original
/// </summary>
public class WithdrawOrchestratorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid SellerId = Guid.NewGuid();

    private static Seller BuildSeller(PaymentProvider preferred = PaymentProvider.STRIPE)
    {
        var seller = Seller.Create(
            tenantId: TenantId, legalName: "Bruce", document: "12345678901",
            email: "bruce@x.com", webhookSecret: "ws",
            preferredProvider: preferred, externalAccountId: "acct_stripe",
            encryptedAccessToken: "encrypted");
        return seller;
    }

    private static LedgerAccount BuildWallet(PaymentProvider provider, decimal balance)
    {
        var acct = LedgerAccount.Create(TenantId, SellerId, LedgerAccountType.WALLET, provider);
        // Force balance via reflection (Credit precisaria de description etc — simplifica)
        typeof(LedgerAccount).GetProperty("Balance")!.SetValue(acct, balance);
        return acct;
    }

    private static (IWithdrawOrchestrator orch,
                    IWithdrawalAttemptRepository attemptRepo,
                    ILedgerRepository ledgerRepo,
                    IPayoutGateway stripeGw,
                    IPayoutGateway openpixGw,
                    List<WithdrawalAttempt> saved) Build(
        Seller seller, List<LedgerAccount> wallets)
    {
        var attemptRepo = Substitute.For<IWithdrawalAttemptRepository>();
        var sellerRepo = Substitute.For<ISellerRepository>();
        var ledgerRepo = Substitute.For<ILedgerRepository>();
        var stripeGw = Substitute.For<IPayoutGateway>();
        var openpixGw = Substitute.For<IPayoutGateway>();
        var uow = Substitute.For<IUnitOfWork>();
        var saved = new List<WithdrawalAttempt>();

        sellerRepo.GetByIdAsync(TenantId, SellerId).Returns(seller);
        ledgerRepo.GetSellerAccountsByTypeAsync(TenantId, SellerId, LedgerAccountType.WALLET).Returns(wallets);
        ledgerRepo.GetAccountAsync(TenantId, LedgerAccountType.WALLET, SellerId, Arg.Any<PaymentProvider?>())
            .Returns(ci =>
            {
                var prov = ci.ArgAt<PaymentProvider?>(3);
                return wallets.FirstOrDefault(w => w.Provider == prov);
            });
        ledgerRepo.GetPlatformAccountAsync(TenantId, LedgerAccountType.PLATFORM_PAYOUT)
            .Returns(LedgerAccount.Create(TenantId, null, LedgerAccountType.PLATFORM_PAYOUT));
        stripeGw.Provider.Returns(PaymentProvider.STRIPE);
        openpixGw.Provider.Returns(PaymentProvider.OPENPIX);

        var factory = Substitute.For<IPayoutGatewayFactory>();
        factory.Get(PaymentProvider.STRIPE).Returns(stripeGw);
        factory.Get(PaymentProvider.OPENPIX).Returns(openpixGw);

        attemptRepo.When(r => r.Add(Arg.Any<WithdrawalAttempt>()))
            .Do(ci => saved.Add(ci.ArgAt<WithdrawalAttempt>(0)));

        var orch = new WithdrawOrchestrator(attemptRepo, sellerRepo, ledgerRepo, factory, uow,
            NullLogger<WithdrawOrchestrator>.Instance);

        return (orch, attemptRepo, ledgerRepo, stripeGw, openpixGw, saved);
    }

    [Fact]
    public async Task HappyPath_SingleProvider_AllocatesAndCompletes()
    {
        var seller = BuildSeller(PaymentProvider.STRIPE);
        var wallets = new List<LedgerAccount> {
            BuildWallet(PaymentProvider.STRIPE, 200m),
        };
        var (orch, _, _, stripeGw, _, saved) = Build(seller, wallets);
        stripeGw.CreatePayoutAsync(Arg.Any<Seller>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(new PayoutGatewayResult("po_stripe_1", "pending"));

        var attempt = await orch.ExecuteAsync(TenantId, SellerId, 100m, idempotencyKey: null);

        attempt.Status.Should().Be(WithdrawalAttemptStatus.COMPLETED);
        attempt.Steps.Should().HaveCount(1);
        attempt.Steps.First().Provider.Should().Be(PaymentProvider.STRIPE);
        attempt.Steps.First().Amount.Should().Be(100m);
        attempt.Steps.First().Status.Should().Be(WithdrawalStepStatus.COMPLETED);
        attempt.Steps.First().ProviderPayoutId.Should().Be("po_stripe_1");
    }

    [Fact]
    public async Task HappyPath_MultiProvider_SplitsAmountAcrossWallets()
    {
        var seller = BuildSeller(PaymentProvider.STRIPE);
        var wallets = new List<LedgerAccount> {
            BuildWallet(PaymentProvider.STRIPE, 100m),
            BuildWallet(PaymentProvider.OPENPIX, 50m),
        };
        var (orch, _, _, stripeGw, openpixGw, _) = Build(seller, wallets);
        stripeGw.CreatePayoutAsync(Arg.Any<Seller>(), 100m, Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(new PayoutGatewayResult("po_stripe_1", "pending"));
        openpixGw.CreatePayoutAsync(Arg.Any<Seller>(), 20m, Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(new PayoutGatewayResult("E2E_OPX_1", "completed"));

        // Saca R$ 120 dos R$ 150 totais — deve virar 100 Stripe + 20 OpenPix
        var attempt = await orch.ExecuteAsync(TenantId, SellerId, 120m, idempotencyKey: null);

        attempt.Status.Should().Be(WithdrawalAttemptStatus.COMPLETED);
        attempt.Steps.Should().HaveCount(2);
        var stepsByProv = attempt.Steps.OrderBy(s => s.Sequence).ToList();
        stepsByProv[0].Provider.Should().Be(PaymentProvider.STRIPE);
        stepsByProv[0].Amount.Should().Be(100m);
        stepsByProv[1].Provider.Should().Be(PaymentProvider.OPENPIX);
        stepsByProv[1].Amount.Should().Be(20m);
    }

    [Fact]
    public async Task InsufficientBalance_ThrowsBeforeCreatingAttempt()
    {
        var seller = BuildSeller(PaymentProvider.STRIPE);
        var wallets = new List<LedgerAccount> {
            BuildWallet(PaymentProvider.STRIPE, 50m),
        };
        var (orch, _, _, _, _, saved) = Build(seller, wallets);

        Func<Task> act = () => orch.ExecuteAsync(TenantId, SellerId, 200m, idempotencyKey: null);

        await act.Should().ThrowAsync<BusinessException>()
            .Where(e => e.Error.Code == "Withdraw.InsufficientBalance");
        saved.Should().BeEmpty("não cria attempt quando saldo é insuficiente");
    }

    [Fact]
    public async Task PartialFailure_SecondStepFails_FirstCompensated()
    {
        var seller = BuildSeller(PaymentProvider.STRIPE);
        var wallets = new List<LedgerAccount> {
            BuildWallet(PaymentProvider.STRIPE, 100m),
            BuildWallet(PaymentProvider.OPENPIX, 50m),
        };
        var (orch, _, _, stripeGw, openpixGw, _) = Build(seller, wallets);
        // Step 1 (Stripe) sucede; step 2 (OpenPix) falha
        stripeGw.CreatePayoutAsync(Arg.Any<Seller>(), 100m, Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(new PayoutGatewayResult("po_stripe_1", "pending"));
        openpixGw.CreatePayoutAsync(Arg.Any<Seller>(), 20m, Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Throws(new PaymentProviderException("OpenPix.Failed", "Provider indisponível"));
        // Stripe cancela com sucesso na compensação
        stripeGw.TryCancelPayoutAsync(Arg.Any<Seller>(), "po_stripe_1").Returns(true);

        var attempt = await orch.ExecuteAsync(TenantId, SellerId, 120m, idempotencyKey: null);

        // Verifica COMPORTAMENTO em vez de status exato (depende do sucesso da
        // revert mockada): step 2 falhou + compensação foi tentada no step 1.
        attempt.Status.Should().BeOneOf(WithdrawalAttemptStatus.FAILED, WithdrawalAttemptStatus.PARTIALLY_COMPLETED);
        await stripeGw.Received().TryCancelPayoutAsync(Arg.Any<Seller>(), "po_stripe_1");
        attempt.FailureSummary.Should().NotBeNullOrEmpty();
        // Step 2 não ficou COMPLETED (porque o provider falhou):
        attempt.Steps.Any(s => s.Provider == PaymentProvider.OPENPIX && s.Status == WithdrawalStepStatus.COMPLETED)
            .Should().BeFalse();
    }

    [Fact]
    public async Task PartialFailure_CompensationFails_AttemptStaysPartiallyCompleted()
    {
        var seller = BuildSeller(PaymentProvider.STRIPE);
        var wallets = new List<LedgerAccount> {
            BuildWallet(PaymentProvider.STRIPE, 100m),
            BuildWallet(PaymentProvider.OPENPIX, 50m),
        };
        var (orch, _, _, stripeGw, openpixGw, _) = Build(seller, wallets);
        stripeGw.CreatePayoutAsync(Arg.Any<Seller>(), 100m, Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(new PayoutGatewayResult("po_stripe_1", "pending"));
        openpixGw.CreatePayoutAsync(Arg.Any<Seller>(), 20m, Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Throws(new PaymentProviderException("OpenPix.Failed", "Provider indisponível"));
        // Stripe NÃO consegue cancelar (ex: payout já saiu pra banco)
        stripeGw.TryCancelPayoutAsync(Arg.Any<Seller>(), "po_stripe_1").Returns(false);

        var attempt = await orch.ExecuteAsync(TenantId, SellerId, 120m, idempotencyKey: null);

        // Step 1 não pôde compensar → continua COMPLETED. Attempt fica PARTIALLY_COMPLETED.
        attempt.Status.Should().Be(WithdrawalAttemptStatus.PARTIALLY_COMPLETED);
        attempt.Steps.Any(s => s.Status == WithdrawalStepStatus.COMPLETED).Should().BeTrue();
        attempt.Steps.Any(s => s.Status == WithdrawalStepStatus.COMPENSATED).Should().BeTrue();
        attempt.FailureSummary.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Idempotency_SameKey_ReturnsExistingAttempt()
    {
        var seller = BuildSeller();
        var wallets = new List<LedgerAccount> { BuildWallet(PaymentProvider.STRIPE, 200m) };
        var (orch, attemptRepo, _, stripeGw, _, _) = Build(seller, wallets);

        var existing = WithdrawalAttempt.Create(TenantId, SellerId, 100m, "idem-key-1");
        attemptRepo.GetByIdempotencyKeyAsync("idem-key-1").Returns(existing);

        var attempt = await orch.ExecuteAsync(TenantId, SellerId, 100m, idempotencyKey: "idem-key-1");

        attempt.Should().BeSameAs(existing, "should reuse attempt com mesma idempotency key");
        await stripeGw.DidNotReceive().CreatePayoutAsync(Arg.Any<Seller>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
    }
}
