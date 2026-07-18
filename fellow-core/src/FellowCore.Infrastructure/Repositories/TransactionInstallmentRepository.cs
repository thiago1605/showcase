using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class TransactionInstallmentRepository(AppDbContext _context) : ITransactionInstallmentRepository
{
    public void AddRange(IEnumerable<TransactionInstallment> installments)
        => _context.TransactionInstallments.AddRange(installments);

    public async Task<List<InstallmentReleaseSlot>> GetReleaseScheduleAsync(
        Guid tenantId, Guid sellerId, DateTime referenceDate, int maxDays = 400)
    {
        var horizon = referenceDate.AddDays(maxDays);

        // JOIN com Transactions pra filtrar por SellerId (não denormalizado na installment).
        // Excluímos TXs REFUNDED/VOIDED — installments delas não vão liberar mesmo.
        return await (
            from inst in _context.TransactionInstallments.AsNoTracking()
            join tx in _context.Transactions.AsNoTracking()
                on inst.TransactionId equals tx.Id
            where inst.TenantId == tenantId
                && tx.SellerId == sellerId
                && inst.Status == SettlementStatus.PENDING
                && inst.ExpectedReleaseDate > referenceDate
                && inst.ExpectedReleaseDate <= horizon
                && (tx.Status == TransactionStatus.CAPTURED || tx.Status == TransactionStatus.AUTHORIZED)
            group inst by inst.ExpectedReleaseDate.Date into g
            orderby g.Key
            select new InstallmentReleaseSlot(g.Key, g.Sum(x => x.NetAmount))
        ).ToListAsync();
    }

    public async Task<List<PendingInstallmentBatch>> GetDueForSettlementAsync(DateTime referenceDate)
    {
        // Agrupado por (TenantId, SellerId) pra processor mover tudo de uma vez.
        // Carrega IDs pra que o MarkSettledAsync subsequente saiba exatamente o que liquidar.
        return await (
            from inst in _context.TransactionInstallments.AsNoTracking()
            join tx in _context.Transactions.AsNoTracking()
                on inst.TransactionId equals tx.Id
            where inst.Status == SettlementStatus.PENDING
                && inst.ExpectedReleaseDate <= referenceDate
                && tx.SellerId != null
                && (tx.Status == TransactionStatus.CAPTURED || tx.Status == TransactionStatus.AUTHORIZED)
            group new { inst, tx } by new { inst.TenantId, SellerId = tx.SellerId!.Value } into g
            select new PendingInstallmentBatch(
                g.Key.TenantId,
                g.Key.SellerId,
                g.Sum(x => x.inst.NetAmount),
                g.Select(x => x.inst.Id).ToList()
            )
        ).ToListAsync();
    }

    public async Task MarkSettledAsync(IEnumerable<Guid> installmentIds, DateTime referenceDate)
    {
        var ids = installmentIds.ToList();
        if (ids.Count == 0) return;

        // ExecuteUpdateAsync evita carregar entidades no tracker.
        // Filter STATUS = PENDING garante idempotência (já settled não muda).
        await _context.TransactionInstallments
            .Where(i => ids.Contains(i.Id) && i.Status == SettlementStatus.PENDING)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.Status, SettlementStatus.SETTLED)
                .SetProperty(i => i.SettledAt, referenceDate)
                .SetProperty(i => i.UpdatedAt, referenceDate));
    }

    public async Task<List<TransactionInstallment>> GetByTransactionIdAsync(Guid transactionId)
        => await _context.TransactionInstallments
            .Where(i => i.TransactionId == transactionId)
            .OrderBy(i => i.Number)
            .ToListAsync();

    public async Task<int> CancelPendingForTransactionAsync(Guid transactionId, DateTime referenceDate)
        => await _context.TransactionInstallments
            .Where(i => i.TransactionId == transactionId && i.Status == SettlementStatus.PENDING)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.Status, SettlementStatus.CANCELED)
                .SetProperty(i => i.UpdatedAt, referenceDate));

    public async Task SaveChangesAsync() => await _context.SaveChangesAsync();
}
