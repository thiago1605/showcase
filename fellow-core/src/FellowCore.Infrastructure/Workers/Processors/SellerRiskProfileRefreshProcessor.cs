using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public interface ISellerRiskProfileRefreshProcessor
{
    Task ProcessAsync(CancellationToken ct = default);
}

/// <summary>
/// Atualiza diariamente o <see cref="SellerRiskProfile"/> de cada seller ativo.
/// Drives anti-fraude de ADVANCE (R5: chargeback rate cap) com latência baixa
/// — captura faz 1 SELECT por PK em vez de 2 COUNTs ad-hoc.
///
/// Modo: processa todos os sellers ativos em batches de 500 (configurável).
/// Idempotente: re-rodar no mesmo dia simplesmente sobrescreve com os mesmos números.
/// </summary>
public class SellerRiskProfileRefreshProcessor(
    AppDbContext context,
    ISellerRiskProfileRepository profileRepository,
    ILogger<SellerRiskProfileRefreshProcessor> logger) : ISellerRiskProfileRefreshProcessor
{
    private const int BatchSize = 500;

    public async Task ProcessAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-90);
        logger.LogInformation("[RISK_REFRESH] Iniciando refresh — janela últimos 90 dias");

        var sellerIds = await profileRepository.GetActiveSellerIdsAsync(BatchSize);
        if (sellerIds.Count == 0)
        {
            logger.LogInformation("[RISK_REFRESH] Nenhum seller ativo");
            return;
        }

        int updated = 0, created = 0;
        foreach (var sellerId in sellerIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // 2 queries em paralelo: count + sum em transactions, e count em disputes.
                var txStatsTask = context.Transactions
                    .AsNoTracking()
                    .Where(t => t.SellerId == sellerId
                        && t.CreatedAt >= since
                        && (t.Status == TransactionStatus.CAPTURED
                            || t.Status == TransactionStatus.REFUNDED
                            || t.Status == TransactionStatus.CHARGEBACKERROR))
                    .GroupBy(t => 1)
                    .Select(g => new { Count = g.Count(), Volume = g.Sum(t => t.NetAmount ?? 0m) })
                    .FirstOrDefaultAsync(ct);

                var lostDisputeCountTask = context.Disputes
                    .AsNoTracking()
                    .Where(d => d.Status == DisputeStatus.LOST && d.CreatedAt >= since)
                    .Join(context.Transactions.Where(t => t.SellerId == sellerId),
                        d => d.TransactionId, t => t.Id, (d, t) => d.Id)
                    .CountAsync(ct);

                await Task.WhenAll(txStatsTask, lostDisputeCountTask);

                var captured = txStatsTask.Result?.Count ?? 0;
                var volume = txStatsTask.Result?.Volume ?? 0m;
                var lost = lostDisputeCountTask.Result;

                var existing = await profileRepository.GetBySellerIdAsync(sellerId);
                if (existing == null)
                {
                    var profile = SellerRiskProfile.CreateOrUpdate(sellerId, captured, lost, volume, now);
                    profileRepository.Add(profile);
                    created++;
                }
                else
                {
                    existing.Refresh(captured, lost, volume, now);
                    profileRepository.Update(existing);
                    updated++;
                }
                await profileRepository.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[RISK_REFRESH] Erro processando seller {SellerId}, pulando", sellerId);
            }
        }

        logger.LogInformation(
            "[RISK_REFRESH] Concluído. {Created} criados, {Updated} atualizados, total {Total}",
            created, updated, created + updated);
    }
}
