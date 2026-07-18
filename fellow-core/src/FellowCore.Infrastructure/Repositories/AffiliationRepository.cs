using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class AffiliationRepository(AppDbContext context) : IAffiliationRepository
{
    public void Add(Affiliation affiliation) => context.Affiliations.Add(affiliation);
    public void Update(Affiliation affiliation) => context.Affiliations.Update(affiliation);
    public Task SaveChangesAsync() => context.SaveChangesAsync();

    public async Task<Affiliation?> GetByIdAsync(Guid tenantId, Guid affiliationId)
        => await context.Affiliations
            .Include(a => a.Product)
            .FirstOrDefaultAsync(a => a.Id == affiliationId && a.TenantId == tenantId);

    public async Task<Affiliation?> GetByTrackingCodeAsync(string trackingCode)
        => await context.Affiliations
            .Include(a => a.Product)
            .FirstOrDefaultAsync(a => a.TrackingCode == trackingCode);

    public async Task<Affiliation?> GetActiveByProductAndSellerAsync(Guid productId, Guid affiliateSellerId)
        => await context.Affiliations
            .FirstOrDefaultAsync(a =>
                a.ProductId == productId &&
                a.AffiliateSellerId == affiliateSellerId &&
                (a.Status == AffiliationStatus.PENDING || a.Status == AffiliationStatus.APPROVED));

    public async Task<IReadOnlyDictionary<Guid, AffiliationStatus>> GetStatusByProductIdsAndSellerAsync(
        IReadOnlyList<Guid> productIds,
        Guid affiliateSellerId)
    {
        if (productIds.Count == 0)
            return new Dictionary<Guid, AffiliationStatus>();

        // Pega o status "mais relevante" caso existam múltiplos registros para
        // o mesmo (produto, seller). Priorização: APPROVED > PENDING > REJECTED >
        // REVOKED. O caller mostra essa informação proativamente no card.
        var rows = await context.Affiliations
            .Where(a => a.AffiliateSellerId == affiliateSellerId
                && productIds.Contains(a.ProductId))
            .Select(a => new { a.ProductId, a.Status })
            .ToListAsync();

        // Ordem inversa de prioridade — escrevemos no dict em ordem crescente
        // de prioridade para o melhor sobrescrever. Equivalente a um GroupBy
        // com agregação, mas mais simples e funciona em qualquer provider.
        static int Priority(AffiliationStatus s) => s switch
        {
            AffiliationStatus.APPROVED => 4,
            AffiliationStatus.PENDING => 3,
            AffiliationStatus.REJECTED => 2,
            AffiliationStatus.REVOKED => 1,
            _ => 0,
        };

        var result = new Dictionary<Guid, AffiliationStatus>();
        foreach (var row in rows.OrderBy(r => Priority(r.Status)))
            result[row.ProductId] = row.Status;
        return result;
    }

    public async Task<(IReadOnlyList<Affiliation> Items, int TotalCount)> GetByProductAsync(
        Guid tenantId,
        Guid productId,
        AffiliationStatus? status,
        int skip,
        int take)
    {
        var q = context.Affiliations
            .Include(a => a.AffiliateSeller)
            .Where(a => a.TenantId == tenantId && a.ProductId == productId);
        if (status.HasValue) q = q.Where(a => a.Status == status.Value);

        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(a => a.RequestedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
        return (items, total);
    }

    public async Task<(IReadOnlyList<Affiliation> Items, int TotalCount)> GetBySellerAsync(
        Guid tenantId,
        Guid affiliateSellerId,
        AffiliationStatus? status,
        int skip,
        int take)
    {
        var q = context.Affiliations
            .Include(a => a.Product)
            .Where(a => a.TenantId == tenantId && a.AffiliateSellerId == affiliateSellerId);
        if (status.HasValue) q = q.Where(a => a.Status == status.Value);

        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(a => a.RequestedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
        return (items, total);
    }

    public async Task<IReadOnlyList<AffiliateLeaderboardEntry>> GetLeaderboardAsync(
        Guid tenantId, Guid productId, int limit)
    {
        if (limit < 1) limit = 10;
        if (limit > 100) limit = 100;

        var productRef = $"product:{productId}";

        // Lista todas as afiliações APPROVED do produto pra ter o conjunto de
        // recipientIds candidatos. Sem isso, o GROUP BY ficaria órfão de
        // metadata (nome do seller, affiliation id) pq TransactionSplit.RecipientId
        // é só string genérica.
        var affsApproved = await context.Affiliations
            .Where(a => a.TenantId == tenantId && a.ProductId == productId &&
                        a.Status == AffiliationStatus.APPROVED)
            .Include(a => a.AffiliateSeller)
            .ToListAsync();
        if (affsApproved.Count == 0) return [];

        // Constrói o set de recipientIds (str) pra IN() filtragem eficiente.
        var recipientIds = affsApproved
            .Select(a => a.AffiliateSellerId.ToString())
            .ToHashSet();

        // Agrega TX por RecipientId — uma única query, sem N+1. Filtros: TX
        // do produto, status final captured/refunded, splits do conjunto de
        // afiliados aprovados.
        var rows = await context.TransactionSplits
            .Where(s => recipientIds.Contains(s.RecipientId))
            .Join(context.Transactions,
                  s => s.TransactionId,
                  t => t.Id,
                  (s, t) => new
                  {
                      s.RecipientId,
                      s.Amount,
                      t.TenantId,
                      TxAmount = t.Amount,
                      t.Status,
                      t.ExternalReferenceId,
                  })
            .Where(x => x.TenantId == tenantId
                     && x.ExternalReferenceId == productRef
                     && (x.Status == TransactionStatus.CAPTURED
                         || x.Status == TransactionStatus.REFUNDED))
            .GroupBy(x => x.RecipientId)
            .Select(g => new
            {
                RecipientId = g.Key,
                Sales = g.Count(),
                Tpv = g.Sum(x => x.TxAmount),
                Earnings = g.Sum(x => x.Amount),
            })
            .ToListAsync();

        // Mapeia RecipientId → Affiliation pra preencher nome + id da afiliação.
        // Lista final ordenada por TPV desc, limit aplicado client-side (data já
        // restrita ao set de afiliados aprovados, então tamanho controlado).
        var byRecipient = affsApproved
            .ToDictionary(a => a.AffiliateSellerId.ToString());

        return rows
            .Where(r => byRecipient.ContainsKey(r.RecipientId))
            .Select(r =>
            {
                var aff = byRecipient[r.RecipientId];
                return new AffiliateLeaderboardEntry(
                    AffiliationId: aff.Id,
                    AffiliateSellerId: aff.AffiliateSellerId,
                    AffiliateName: aff.AffiliateSeller?.TradeName ?? aff.AffiliateSeller?.LegalName,
                    SalesCount: r.Sales,
                    Tpv: r.Tpv,
                    Earnings: r.Earnings);
            })
            .OrderByDescending(e => e.Tpv)
            .Take(limit)
            .ToList();
    }

    public async Task<bool> RecordClickAsync(AffiliateClickEvent clickEvent)
    {
        // Dedup window de 1h: se já existe click da mesma fingerprint pra
        // mesma afiliação na última hora, suprime. Sem dedup, refresh / navegar
        // de volta inflaria click count e prejudicaria a conversion rate.
        var since = clickEvent.CreatedAt.AddHours(-1);
        var exists = await context.AffiliateClickEvents.AnyAsync(c =>
            c.AffiliationId == clickEvent.AffiliationId &&
            c.Fingerprint == clickEvent.Fingerprint &&
            c.CreatedAt >= since);
        if (exists) return false;

        context.AffiliateClickEvents.Add(clickEvent);
        await context.SaveChangesAsync();
        return true;
    }

    public async Task<AffiliateMetricsSnapshot> GetAffiliateMetricsAsync(
        Guid tenantId,
        Guid affiliateSellerId,
        Guid productId,
        Guid affiliationId,
        int days)
    {
        // RecipientId é string no schema (genérico pra suportar non-seller
        // recipients no futuro). Pra esse use case, sempre é Guid do seller.
        var recipientIdStr = affiliateSellerId.ToString();
        var productRef = $"product:{productId}";
        // Janela móvel `days`. Caller deve validar (7/30/90) — repo aceita
        // qualquer positivo. Anterior = mesma duração imediatamente antes.
        if (days < 1) days = 30;
        var since30 = DateTime.UtcNow.AddDays(-days);

        // Join TransactionSplit × Transaction. Filtros aplicados:
        //  - RecipientId = afiliado (string compare pq schema é string genérico)
        //  - TX desse produto específico (ExternalReferenceId == "product:{id}")
        //  - TX desse tenant (defense in depth — RecipientId é global)
        //  - Status da TX captured/refunded (em algum momento foi sale; refund
        //    posterior não apaga o fluxo histórico)
        // Pra Earnings pendentes: splits em PENDING/RESERVED/PROCESSING — TX
        // capturada mas split ainda não distribuído pro SELLER_WALLET (ex:
        // settlement delay de cartão D+30).
        var rows = await context.TransactionSplits
            .Where(s => s.RecipientId == recipientIdStr)
            .Join(context.Transactions,
                  s => s.TransactionId,
                  t => t.Id,
                  (s, t) => new
                  {
                      SplitAmount = s.Amount,
                      SplitStatus = s.Status,
                      TxAmount = t.Amount,
                      TxStatus = t.Status,
                      TxCreatedAt = t.CreatedAt,
                      TenantId = t.TenantId,
                      TxRef = t.ExternalReferenceId,
                  })
            .Where(x => x.TenantId == tenantId
                     && x.TxRef == productRef
                     && (x.TxStatus == TransactionStatus.CAPTURED
                         || x.TxStatus == TransactionStatus.REFUNDED))
            .ToListAsync();

        // Janela atual (`days`) e anterior (mesma duração imediatamente antes).
        var since60 = DateTime.UtcNow.AddDays(-days * 2);
        var rows30d = rows.Where(r => r.TxCreatedAt >= since30).ToList();
        var rowsPrev30d = rows.Where(r => r.TxCreatedAt < since30 && r.TxCreatedAt >= since60).ToList();

        // Split é "pendente" enquanto não foi PAID (= já transferido pro
        // SELLER_WALLET via SplitProcessor) nem FAILED. Valores possíveis no
        // enum atual: PENDING (criado), SCHEDULED (TX captured, aguardando
        // settlement window), PAID (distribuído), FAILED (rollback).
        decimal pending = rows
            .Where(r => r.SplitStatus == SplitStatus.PENDING
                     || r.SplitStatus == SplitStatus.SCHEDULED)
            .Sum(r => r.SplitAmount);

        // Click counts: total all-time + período atual + anterior + timeseries
        // diário. 4 queries simples — todas usam o index (AffiliationId, CreatedAt).
        var clicksAllTime = await context.AffiliateClickEvents
            .Where(c => c.AffiliationId == affiliationId)
            .CountAsync();
        var clicks30d = await context.AffiliateClickEvents
            .Where(c => c.AffiliationId == affiliationId && c.CreatedAt >= since30)
            .CountAsync();
        var clicksPrev30d = await context.AffiliateClickEvents
            .Where(c => c.AffiliationId == affiliationId
                     && c.CreatedAt < since30 && c.CreatedAt >= since60)
            .CountAsync();

        // Clicks por dia — pega timestamps brutos da janela e agrupa em
        // memória pelo "diasAgo" no helper. Tamanho do array = `days`.
        var clicksRaw = await context.AffiliateClickEvents
            .Where(c => c.AffiliationId == affiliationId && c.CreatedAt >= since30)
            .Select(c => c.CreatedAt)
            .ToListAsync();
        var clicksByDay = BuildTimeseries(clicksRaw, days);
        var salesByDay = BuildTimeseries(rows30d.Select(r => r.TxCreatedAt), days);

        return new AffiliateMetricsSnapshot(
            PeriodDays: days,
            SalesInPeriod: rows30d.Count,
            TpvInPeriod: rows30d.Sum(r => r.TxAmount),
            EarningsInPeriod: rows30d.Sum(r => r.SplitAmount),
            SalesAllTime: rows.Count,
            TpvAllTime: rows.Sum(r => r.TxAmount),
            EarningsAllTime: rows.Sum(r => r.SplitAmount),
            EarningsPending: pending,
            ClicksInPeriod: clicks30d,
            ClicksAllTime: clicksAllTime,
            ClicksByDay: clicksByDay,
            SalesByDay: salesByDay,
            PreviousSalesInPeriod: rowsPrev30d.Count,
            PreviousTpvInPeriod: rowsPrev30d.Sum(r => r.TxAmount),
            PreviousEarningsInPeriod: rowsPrev30d.Sum(r => r.SplitAmount),
            PreviousClicksInPeriod: clicksPrev30d);
    }

    /// <summary>
    /// Constrói array de `days` ints com a contagem de eventos por dia.
    /// Ordem: índice 0 = (days-1) dias atrás, último = hoje. Dias sem eventos
    /// viram 0 — sparkline renderiza sem gaps.
    /// </summary>
    private static int[] BuildTimeseries(IEnumerable<DateTime> timestamps, int days)
    {
        var result = new int[days];
        var today = DateTime.UtcNow.Date;
        foreach (var ts in timestamps)
        {
            var daysAgo = (int)(today - ts.Date).TotalDays;
            if (daysAgo < 0 || daysAgo >= days) continue;
            // Ordem antigo → novo: índice = (days-1) - daysAgo.
            // Ex: hoje (daysAgo=0) → último índice (days-1).
            result[(days - 1) - daysAgo]++;
        }
        return result;
    }
}
