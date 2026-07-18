using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public interface IStripeAdvanceReconciler
{
    Task ProcessAsync(CancellationToken ct = default);
}

/// <summary>
/// Reconciliação ADVANCE baseada em Stripe balance_transactions reais (mais preciso
/// que o <see cref="AdvanceSettlementReconciler"/> baseado em tempo).
///
/// Algoritmo:
///   1. Pra cada tenant, cursor = TenantConfig.LastStripeAdvanceReconcileAt (default = -7 dias)
///   2. Chama Stripe ListBalanceTransactionsAsync(createdGte=cursor, type="charge")
///   3. Pra cada balance_txn.source (ch_xxx), lookup Transaction.StripeChargeId
///   4. Se for TX em ADVANCE não totalmente recuperada:
///      - fraction = balance_txn.amount / (TX.NetAmount × 100)
///      - parcelasNovas = round(fraction × Installments)
///      - se &gt; AdvanceRecoveredInstallmentCount: credita reserve + decrementa exposure
///   5. Avança cursor pro último balance_txn.created processado
///
/// Idempotência: TX.AdvanceRecoveredInstallmentCount monotônico + cursor avança.
/// Re-rodar o job no mesmo dia processa só novos eventos.
///
/// Habilitado via config <c>AdvanceReconciler:UseStripe=true</c>. Default false —
/// mantém o time-proxy como fallback que não depende de API externa.
/// </summary>
public class StripeAdvanceReconciler(
    AppDbContext context,
    ITenantRepository tenantRepository,
    ISellerRepository sellerRepository,
    IStripeApiClient stripeApi,
    IConfiguration configuration,
    ILogger<StripeAdvanceReconciler> logger) : IStripeAdvanceReconciler
{
    public async Task ProcessAsync(CancellationToken ct = default)
    {
        bool enabled = configuration.GetValue("AdvanceReconciler:UseStripe", false);
        if (!enabled)
        {
            logger.LogDebug("[STRIPE_ADV_RECON] Desabilitado via config (AdvanceReconciler:UseStripe=false). Usando time-proxy.");
            return;
        }

        var stripeKey = configuration["Stripe:SecretKey"];
        if (string.IsNullOrEmpty(stripeKey))
        {
            logger.LogWarning("[STRIPE_ADV_RECON] Stripe:SecretKey ausente — pulando");
            return;
        }

        var tenants = await tenantRepository.GetAllAsync();
        var now = DateTime.UtcNow;
        var defaultLookback = now.AddDays(-7);

        foreach (var tenant in tenants)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var tenantWithConfig = await tenantRepository.GetByIdWithConfigAsync(tenant.Id);
                if (tenantWithConfig?.Config == null) continue;

                var cursor = tenantWithConfig.Config.LastStripeAdvanceReconcileAt ?? defaultLookback;
                var createdGte = new DateTimeOffset(cursor).ToUnixTimeSeconds();

                logger.LogInformation("[STRIPE_ADV_RECON] Tenant {Id} processando desde {Cursor}",
                    tenant.Id, cursor);

                var response = await stripeApi.ListBalanceTransactionsAsync(
                    stripeKey, createdGte: createdGte, type: "charge", limit: 100);

                if (response.Data == null || response.Data.Count == 0)
                {
                    tenantWithConfig.Config.SetLastStripeAdvanceReconcileAt(now);
                    await tenantRepository.SaveChangesAsync();
                    continue;
                }

                long maxCreated = createdGte;
                int processed = 0;
                long totalCreditedCents = 0;

                foreach (var bt in response.Data.OrderBy(b => b.Created))
                {
                    if (string.IsNullOrEmpty(bt.Source)) continue;
                    if (bt.Status != "available") continue; // só fundos liberados de fato

                    // Lookup TX pela charge ID. Filter pra ADVANCE não totalmente recuperada.
                    var tx = await context.Transactions
                        .Where(t => t.StripeChargeId == bt.Source
                            && t.SettlementMode == SettlementMode.ADVANCE
                            && t.AdvanceRecoveredInstallmentCount < t.Installments
                            && t.NetAmount.HasValue
                            && t.SellerId.HasValue)
                        .FirstOrDefaultAsync(ct);

                    if (tx == null)
                    {
                        if (bt.Created > maxCreated) maxCreated = bt.Created;
                        continue;
                    }

                    // Calcula fração das parcelas recuperadas baseada em amount real recebido.
                    // Conservador: floor, evita over-credit em arredondamento.
                    var netCents = (long)Math.Round(tx.NetAmount!.Value * 100m);
                    if (netCents <= 0) continue;

                    var fraction = (decimal)bt.Amount / netCents;
                    var totalParcelsRecovered = (int)Math.Floor(fraction * tx.Installments);
                    var newlyRecovered = totalParcelsRecovered - tx.AdvanceRecoveredInstallmentCount;

                    if (newlyRecovered <= 0)
                    {
                        if (bt.Created > maxCreated) maxCreated = bt.Created;
                        continue;
                    }

                    var advanceFee = tx.AdvanceFeeAmount ?? 0m;
                    var perInstallmentNet = (tx.NetAmount!.Value - advanceFee) / tx.Installments;
                    var amountToRecover = perInstallmentNet * newlyRecovered;
                    var centsToRecover = (long)Math.Round(amountToRecover * 100m);

                    tenantWithConfig.Config.CreditAdvanceReserve(centsToRecover);
                    var seller = await sellerRepository.GetByIdAsync(tx.TenantId, tx.SellerId!.Value);
                    if (seller != null)
                    {
                        seller.DecreaseAdvanceExposure(amountToRecover);
                        sellerRepository.Update(seller);
                    }
                    tx.IncrementRecoveredInstallments(newlyRecovered);
                    await context.SaveChangesAsync(ct);

                    processed++;
                    totalCreditedCents += centsToRecover;
                    if (bt.Created > maxCreated) maxCreated = bt.Created;

                    logger.LogInformation(
                        "[STRIPE_ADV_RECON] TX {TxId}: +{N} parcela(s) (R${Amount}) confirmadas via balance_txn {Bt}",
                        tx.Id, newlyRecovered, amountToRecover, bt.Id);
                }

                // Avança cursor pro último timestamp processado + 1s pra não re-processar.
                var newCursor = DateTimeOffset.FromUnixTimeSeconds(maxCreated + 1).UtcDateTime;
                tenantWithConfig.Config.SetLastStripeAdvanceReconcileAt(newCursor);
                await tenantRepository.SaveChangesAsync();

                logger.LogInformation(
                    "[STRIPE_ADV_RECON] Tenant {Id} concluído: {N} TXs processadas, R${Total} creditado, cursor → {Cursor}",
                    tenant.Id, processed, totalCreditedCents / 100m, newCursor);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[STRIPE_ADV_RECON] Erro processando tenant {TenantId}", tenant.Id);
            }
        }
    }
}
