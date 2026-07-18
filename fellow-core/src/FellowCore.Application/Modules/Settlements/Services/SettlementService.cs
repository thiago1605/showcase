using FellowCore.Domain.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Settlements.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Settlements.Services;

/// <summary>
/// Roda diariamente via Hangfire (cron: 0 0 * * *) e libera parcelas maduras.
///
/// Fluxo:
///   1. Busca <see cref="ITransactionInstallmentRepository.GetDueForSettlementAsync"/>
///      — parcelas PENDING com ExpectedReleaseDate <= agora, agrupadas por seller.
///   2. Pra cada (tenant, seller): TransferFundsAsync move o total das parcelas
///      maduras de FUTURE_RECEIVABLES → WALLET (ledger double-entry).
///   3. MarkSettledAsync atualiza o status das parcelas processadas.
///
/// Importante: a unidade de settlement passou a ser **parcela**, não TX. Crédito 6x
/// fica 6 ciclos no settlement (1 parcela por mês). Antes liquidava tudo em D+180.
/// </summary>
public class SettlementService(
    ITransactionInstallmentRepository installmentRepository,
    IServiceScopeFactory scopeFactory,
    ILogger<SettlementService> logger) : ISettlementService
{
    private const int MaxParallelism = 5;

    public async Task ProcessDailySettlementsAsync()
    {
        logger.LogInformation("Buscando parcelas maduras para liquidar");

        var now = DateTime.UtcNow;
        var dueBatches = await installmentRepository.GetDueForSettlementAsync(referenceDate: now);

        if (dueBatches == null || dueBatches.Count == 0)
        {
            logger.LogInformation("Nenhuma parcela madura encontrada");
            return;
        }

        logger.LogInformation(
            "Encontrei {SellerCount} sellers com parcelas maduras ({TotalInstallments} parcelas no total)",
            dueBatches.Count, dueBatches.Sum(b => b.InstallmentIds.Count));

        await Parallel.ForEachAsync(
            dueBatches.Where(b => b.TotalAmount > 0),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism },
            async (batch, ct) =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var ledgerService = scope.ServiceProvider.GetRequiredService<ILedgerService>();
                var installRepo = scope.ServiceProvider.GetRequiredService<ITransactionInstallmentRepository>();

                logger.LogInformation(
                    "Liberando R$ {Amount} ({InstallmentCount} parcelas) para seller {SellerId}",
                    batch.TotalAmount, batch.InstallmentIds.Count, batch.SellerId);

                await unitOfWork.BeginAsync();
                try
                {
                    // Move o total de FUTURE_RECEIVABLES → WALLET.
                    await ledgerService.TransferFundsAsync(
                        tenantId: batch.TenantId,
                        sellerId: batch.SellerId,
                        amount: batch.TotalAmount);

                    // Marca as parcelas como SETTLED (idempotente via filter Status = PENDING).
                    await installRepo.MarkSettledAsync(batch.InstallmentIds, now);

                    await unitOfWork.CommitAsync();

                    logger.LogInformation(
                        "Liquidação concluída pra seller {SellerId}: R$ {Amount}", batch.SellerId, batch.TotalAmount);
                }
                catch (Exception ex)
                {
                    await unitOfWork.RollbackAsync();
                    logger.LogError(ex,
                        "Falha ao liquidar parcelas do seller {SellerId}. Continuo com os demais.",
                        batch.SellerId);
                }
            });
    }
}
