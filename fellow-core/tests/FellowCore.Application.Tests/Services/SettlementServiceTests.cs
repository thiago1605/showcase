using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Settlements.Services;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Tests.Services;

public class SettlementServiceTests
{
    private readonly ITransactionInstallmentRepository _installmentRepo = Substitute.For<ITransactionInstallmentRepository>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly ILogger<SettlementService> _logger = Substitute.For<ILogger<SettlementService>>();
    private readonly SettlementService _sut;

    // Scoped dependencies
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ILedgerService _ledgerService = Substitute.For<ILedgerService>();
    private readonly ITransactionInstallmentRepository _scopedInstallmentRepo = Substitute.For<ITransactionInstallmentRepository>();

    public SettlementServiceTests()
    {
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        serviceProvider.GetService(typeof(IUnitOfWork)).Returns(_unitOfWork);
        serviceProvider.GetService(typeof(ILedgerService)).Returns(_ledgerService);
        serviceProvider.GetService(typeof(ITransactionInstallmentRepository)).Returns(_scopedInstallmentRepo);

        scope.ServiceProvider.Returns(serviceProvider);
        _scopeFactory.CreateAsyncScope().Returns(new AsyncServiceScope(scope));

        _sut = new SettlementService(_installmentRepo, _scopeFactory, _logger);
    }

    [Fact]
    public async Task ProcessDailySettlementsAsync_NoDueInstallments_ReturnsEarly()
    {
        _installmentRepo.GetDueForSettlementAsync(Arg.Any<DateTime>())
            .Returns(new List<PendingInstallmentBatch>());

        await _sut.ProcessDailySettlementsAsync();

        _scopeFactory.DidNotReceive().CreateAsyncScope();
    }

    [Fact]
    public async Task ProcessDailySettlementsAsync_NullResult_ReturnsEarly()
    {
        _installmentRepo.GetDueForSettlementAsync(Arg.Any<DateTime>())!
            .Returns((List<PendingInstallmentBatch>?)null);

        await _sut.ProcessDailySettlementsAsync();

        _scopeFactory.DidNotReceive().CreateAsyncScope();
    }

    [Fact]
    public async Task ProcessDailySettlementsAsync_ProcessesEachSellerBatch()
    {
        var tenantId = Guid.NewGuid();
        var sellerId1 = Guid.NewGuid();
        var sellerId2 = Guid.NewGuid();
        var ids1 = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var ids2 = new List<Guid> { Guid.NewGuid() };

        _installmentRepo.GetDueForSettlementAsync(Arg.Any<DateTime>())
            .Returns(new List<PendingInstallmentBatch>
            {
                new(tenantId, sellerId1, 500m, ids1),
                new(tenantId, sellerId2, 300m, ids2),
            });

        await _sut.ProcessDailySettlementsAsync();

        await _ledgerService.Received(1).TransferFundsAsync(tenantId, sellerId1, 500m);
        await _ledgerService.Received(1).TransferFundsAsync(tenantId, sellerId2, 300m);
        await _scopedInstallmentRepo.Received(1).MarkSettledAsync(ids1, Arg.Any<DateTime>());
        await _scopedInstallmentRepo.Received(1).MarkSettledAsync(ids2, Arg.Any<DateTime>());
        await _unitOfWork.Received(2).BeginAsync();
        await _unitOfWork.Received(2).CommitAsync();
    }

    [Fact]
    public async Task ProcessDailySettlementsAsync_SkipsZeroAmountBatches()
    {
        var tenantId = Guid.NewGuid();
        _installmentRepo.GetDueForSettlementAsync(Arg.Any<DateTime>())
            .Returns(new List<PendingInstallmentBatch>
            {
                new(tenantId, Guid.NewGuid(), 0m, new List<Guid> { Guid.NewGuid() }),
                new(tenantId, Guid.NewGuid(), 100m, new List<Guid> { Guid.NewGuid() }),
            });

        await _sut.ProcessDailySettlementsAsync();

        await _ledgerService.Received(1).TransferFundsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), 100m);
    }

    [Fact]
    public async Task ProcessDailySettlementsAsync_RollsBackOnLedgerFailure()
    {
        var tenantId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var ids = new List<Guid> { Guid.NewGuid() };

        _installmentRepo.GetDueForSettlementAsync(Arg.Any<DateTime>())
            .Returns(new List<PendingInstallmentBatch>
            {
                new(tenantId, sellerId, 500m, ids),
            });

        _ledgerService.TransferFundsAsync(tenantId, sellerId, 500m)
            .ThrowsAsync(new Exception("Ledger transfer failed"));

        await _sut.ProcessDailySettlementsAsync();

        await _unitOfWork.Received(1).RollbackAsync();
        await _unitOfWork.DidNotReceive().CommitAsync();
        await _scopedInstallmentRepo.DidNotReceive().MarkSettledAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task ProcessDailySettlementsAsync_ContinuesAfterOneFailure()
    {
        var tenantId = Guid.NewGuid();
        var sellerFail = Guid.NewGuid();
        var sellerSuccess = Guid.NewGuid();

        _installmentRepo.GetDueForSettlementAsync(Arg.Any<DateTime>())
            .Returns(new List<PendingInstallmentBatch>
            {
                new(tenantId, sellerFail, 500m, new List<Guid> { Guid.NewGuid() }),
                new(tenantId, sellerSuccess, 300m, new List<Guid> { Guid.NewGuid() }),
            });

        _ledgerService.TransferFundsAsync(tenantId, sellerFail, 500m).ThrowsAsync(new Exception("Fail"));
        _ledgerService.TransferFundsAsync(tenantId, sellerSuccess, 300m).Returns(Task.CompletedTask);

        await _sut.ProcessDailySettlementsAsync();

        await _ledgerService.Received(1).TransferFundsAsync(tenantId, sellerFail, 500m);
        await _ledgerService.Received(1).TransferFundsAsync(tenantId, sellerSuccess, 300m);
    }
}
