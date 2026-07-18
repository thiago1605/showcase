using FellowCore.Application.Common.Interfaces;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public interface IAdvanceSettlementReconciler
{
    Task ProcessAsync(CancellationToken ct = default);
}

/// <summary>
/// Roda diariamente via Hangfire. Pra cada TX ADVANCE não totalmente recuperada,
/// detecta quantas parcelas (no schedule original do customer) já maduraram em
/// D+30, D+60, …, D+30*N — e por cada parcela nova, devolve <c>netAdvanced/N</c>
/// pra reserve do tenant + decrementa <c>seller.AdvanceExposureCurrent</c>.
///
/// Idempotência: <c>Transaction.AdvanceRecoveredInstallmentCount</c> incrementa
/// monotonicamente até bater <c>Installments</c>. Re-rodar o job no mesmo dia
/// não cria duplicidade.
///
/// Premissa do MVP: tempo é proxy de cash flow real (Stripe libera D+30/mês).
/// Em produção, refatorar pra ler <c>balance_transactions</c> da Stripe
/// (já há método <c>ListBalanceTransactionsAsync</c> no <c>IStripeApiClient</c>).
/// </summary>
public class AdvanceSettlementReconciler(
    AppDbContext context,
    ITenantRepository tenantRepository,
    ISellerRepository sellerRepository,
    ILogger<AdvanceSettlementReconciler> logger) : IAdvanceSettlementReconciler
{
    public async Task ProcessAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        logger.LogInformation("[ADVANCE_RECONCILE] Iniciando reconciliação");

        // Carrega TXs ADVANCE em CAPTURED não totalmente recuperadas
        var candidates = await context.Transactions
            .Where(t => t.SettlementMode == SettlementMode.ADVANCE
                && t.Status == TransactionStatus.CAPTURED
                && t.AdvanceRecoveredInstallmentCount < t.Installments
                && t.NetAmount.HasValue
                && t.SellerId.HasValue)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            logger.LogInformation("[ADVANCE_RECONCILE] Nenhuma TX pra reconciliar");
            return;
        }

        logger.LogInformation("[ADVANCE_RECONCILE] {N} TXs candidatas", candidates.Count);

        long totalCreditedCents = 0;
        decimal totalExposureReduced = 0m;

        foreach (var tx in candidates)
        {
            if (ct.IsCancellationRequested) break;

            // Quantas parcelas teriam maturado até agora?
            // Captura = UpdatedAt no momento da última transição CAPTURED;
            // proxy pra capturedAt. Cada parcela = +30 dias.
            var monthsSinceCapture = (int)((now - tx.UpdatedAt).TotalDays / 30);
            var expectedMatured = Math.Min(tx.Installments, monthsSinceCapture);
            var newlyMatured = expectedMatured - tx.AdvanceRecoveredInstallmentCount;

            if (newlyMatured <= 0) continue;

            // 1/N do netAdvancedToSeller pra cada parcela
            var advanceFee = tx.AdvanceFeeAmount ?? 0m;
            var netAdvanced = tx.NetAmount!.Value - advanceFee;
            var perInstallmentNet = netAdvanced / tx.Installments;
            var amountToRecover = perInstallmentNet * newlyMatured;
            var centsToRecover = (long)Math.Round(amountToRecover * 100m);

            try
            {
                var tenant = await tenantRepository.GetByIdWithConfigAsync(tx.TenantId);
                if (tenant?.Config == null)
                {
                    logger.LogWarning("[ADVANCE_RECONCILE] TenantConfig ausente pra {TenantId}, pulando TX {TxId}",
                        tx.TenantId, tx.Id);
                    continue;
                }

                tenant.Config.CreditAdvanceReserve(centsToRecover);

                var seller = await sellerRepository.GetByIdAsync(tx.TenantId, tx.SellerId!.Value);
                if (seller != null)
                {
                    seller.DecreaseAdvanceExposure(amountToRecover);
                    sellerRepository.Update(seller);
                }

                tx.IncrementRecoveredInstallments(newlyMatured);
                await context.SaveChangesAsync(ct);

                totalCreditedCents += centsToRecover;
                totalExposureReduced += amountToRecover;

                logger.LogInformation(
                    "[ADVANCE_RECONCILE] TX {TxId}: +{N} parcela(s) recuperadas (R${Amount}, {Cents}c). " +
                    "Total: {Recovered}/{Total}",
                    tx.Id, newlyMatured, amountToRecover, centsToRecover,
                    tx.AdvanceRecoveredInstallmentCount, tx.Installments);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[ADVANCE_RECONCILE] Erro processando TX {TxId} — pulando", tx.Id);
            }
        }

        logger.LogInformation(
            "[ADVANCE_RECONCILE] Concluído. Reserve creditada: {Cents}c (R${Reais}), exposure reduzida: R${Exposure}",
            totalCreditedCents, totalCreditedCents / 100m, totalExposureReduced);
    }
}
