using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class PaymentLinkRepository(AppDbContext context) : IPaymentLinkRepository
{
    public async Task<PaymentLink?> GetByTokenAsync(string token)
        => await context.Set<PaymentLink>().FirstOrDefaultAsync(l => l.Token == token);

    public async Task<PaymentLink?> GetByIdAsync(Guid tenantId, Guid id)
        => await context.Set<PaymentLink>().FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId);

    public async Task<List<PaymentLink>> GetByTenantAsync(Guid tenantId, Guid? sellerId = null)
    {
        var query = context.Set<PaymentLink>().Where(l => l.TenantId == tenantId);
        if (sellerId.HasValue) query = query.Where(l => l.SellerId == sellerId.Value);
        return await query
            .OrderByDescending(l => l.CreatedAt)
            .Take(200)
            .ToListAsync();
    }

    public void Add(PaymentLink link) => context.Set<PaymentLink>().Add(link);
    public void Update(PaymentLink link) => context.Set<PaymentLink>().Update(link);

    public async Task<PaymentLinkUsageAttempt?> TryReserveUsageAsync(Guid linkId)
    {
        await using var tx = await context.Database.BeginTransactionAsync();
        try
        {
            // MaxUses == null = link ilimitado; nesse caso UsageCount nunca é guardado
            // contra um teto e Active permanece true. Quando há teto, deactiva quando
            // UsageCount+1 >= MaxUses.
            int affected = await context.Set<PaymentLink>()
                .Where(l => l.Id == linkId && l.Active && (l.MaxUses == null || l.UsageCount < l.MaxUses))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(l => l.UsageCount, l => l.UsageCount + 1)
                    .SetProperty(l => l.Active, l => l.MaxUses == null || l.UsageCount + 1 < l.MaxUses));

            if (affected == 0)
            {
                await tx.RollbackAsync();
                return null;
            }

            var attempt = PaymentLinkUsageAttempt.CreateReserved(linkId);
            context.Set<PaymentLinkUsageAttempt>().Add(attempt);
            await context.SaveChangesAsync();

            await tx.CommitAsync();
            return attempt;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<bool> CompleteUsageAttemptAsync(Guid attemptId, Guid transactionId)
    {
        int affected = await context.Set<PaymentLinkUsageAttempt>()
            .Where(a => a.Id == attemptId && a.Status == UsageAttemptStatus.RESERVED)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Status, UsageAttemptStatus.COMPLETED)
                .SetProperty(a => a.TransactionId, transactionId)
                .SetProperty(a => a.CompletedAt, DateTime.UtcNow));
        return affected == 1;
    }

    public async Task FailUsageAttemptAsync(Guid attemptId)
    {
        // Mark the attempt as FAILED — only if still RESERVED (idempotent)
        int affected = await context.Set<PaymentLinkUsageAttempt>()
            .Where(a => a.Id == attemptId && a.Status == UsageAttemptStatus.RESERVED)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Status, UsageAttemptStatus.FAILED)
                .SetProperty(a => a.FailedAt, DateTime.UtcNow));

        if (affected == 0) return; // Already completed or failed — no rollback needed

        // Get the linkId to rollback usage count
        var linkId = await context.Set<PaymentLinkUsageAttempt>()
            .Where(a => a.Id == attemptId)
            .Select(a => a.PaymentLinkId)
            .FirstAsync();

        // Decrement UsageCount and re-activate if needed
        await context.Set<PaymentLink>()
            .Where(l => l.Id == linkId && l.UsageCount > 0)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(l => l.UsageCount, l => l.UsageCount - 1)
                .SetProperty(l => l.Active, true));
    }

    public Task SaveChangesAsync() => context.SaveChangesAsync();

    public async Task<List<(Guid LinkId, string Name, string Token, int Count, decimal Volume)>> GetTopByVolumeAsync(
        Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, int limit)
    {
        // Junta usage attempt COMPLETED → transaction → link, agrupa por link e soma o volume.
        // UsageAttemptStatus.COMPLETED só indica que a transação foi criada com sucesso —
        // não que o dinheiro entrou (PIX/boleto podem ficar pendentes). Filtramos por
        // Transaction.Status == CAPTURED pra refletir só links que de fato converteram.
        var query = from att in context.Set<PaymentLinkUsageAttempt>()
                    where att.Status == UsageAttemptStatus.COMPLETED && att.TransactionId != null
                    join tx in context.Set<Transaction>() on att.TransactionId equals tx.Id
                    join link in context.Set<PaymentLink>() on att.PaymentLinkId equals link.Id
                    where tx.TenantId == tenantId && link.TenantId == tenantId
                          && tx.Status == TransactionStatus.CAPTURED
                    select new { LinkId = link.Id, link.Description, link.Token, tx.Amount, tx.CreatedAt, tx.SellerId };

        if (from.HasValue) query = query.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.CreatedAt <= to.Value);
        if (sellerId.HasValue) query = query.Where(x => x.SellerId == sellerId.Value);

        var grouped = await query
            .GroupBy(x => new { x.LinkId, x.Description, x.Token })
            .Select(g => new
            {
                g.Key.LinkId,
                g.Key.Description,
                g.Key.Token,
                Count = g.Count(),
                Volume = g.Sum(x => x.Amount),
            })
            .OrderByDescending(x => x.Volume)
            .Take(limit)
            .ToListAsync();

        // PaymentLink não tem Name dedicado — usamos Description (livre) com fallback
        // pro token caso o seller não tenha nomeado.
        return grouped.Select(g => (
            g.LinkId,
            string.IsNullOrWhiteSpace(g.Description) ? g.Token : g.Description!,
            g.Token,
            g.Count,
            g.Volume)).ToList();
    }
}
