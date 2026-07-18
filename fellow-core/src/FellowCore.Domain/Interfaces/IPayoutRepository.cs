using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Interfaces;

public interface IPayoutRepository
{
    Task<Payout?> GetByIdAsync(Guid tenantId, Guid id);
    Task<Payout?> GetByIdGlobalAsync(Guid id);
    Task<(IReadOnlyList<Payout> Items, int TotalCount)> GetPagedAsync(
        Guid tenantId, int skip, int take, Guid? sellerId = null, PayoutStatus? status = null);
    Task<List<Payout>> GetForExportAsync(Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, PayoutStatus? status, int limit = 10000);
    IAsyncEnumerable<Payout> StreamForExportAsync(Guid tenantId, DateTime? from, DateTime? to, Guid? sellerId, PayoutStatus? status, int limit = 10000);
    Task<List<Payout>> GetByTenantAndDateRangeAsync(Guid tenantId, DateTime from, DateTime to, PayoutStatus? status = null);
    Task<(int Count, decimal TotalAmount)> GetPendingSummaryAsync(Guid tenantId);
    Task<List<Payout>> GetRetryDueAsync(DateTime utcNow, int limit = 50);
    Task<int> GetStuckProcessingCountAsync(TimeSpan olderThan);

    /// <summary>
    /// Soma do valor BRUTO de payouts criados hoje (UTC), independente do status,
    /// exceto FAILED/CANCELED (não consomem cap). Usado pela guarda do cap diário
    /// global da plataforma (Woovi R$ 48.800 — confirmado pelo suporte).
    /// </summary>
    Task<decimal> GetTodayTotalGrossAsync(DateTime nowUtc);

    /// <summary>
    /// Payouts agendados (ScheduledFor &lt;= now, Status=PENDING) ordenados por
    /// criação (FIFO). Usado pelo processor que esvazia a fila assim que o cap
    /// diário tem espaço ou virou o dia.
    /// </summary>
    Task<List<Payout>> GetScheduledDueAsync(DateTime nowUtc, int limit = 100);

    void Add(Payout payout);
    void Update(Payout payout);
    Task SaveChangesAsync();
}
