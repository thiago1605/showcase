using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class SettlementReportRepository(AppDbContext context) : ISettlementReportRepository
{
    public void Add(SettlementReport report) => context.SettlementReports.Add(report);
    public void AddItem(SettlementReportItem item) => context.SettlementReportItems.Add(item);
    public void AddItems(IEnumerable<SettlementReportItem> items) => context.SettlementReportItems.AddRange(items);

    public Task<SettlementReport?> GetByIdAsync(Guid tenantId, Guid reportId)
        => context.SettlementReports
            .Where(r => r.TenantId == tenantId && r.Id == reportId)
            .FirstOrDefaultAsync();

    public Task<SettlementReport?> GetLatestAsync(Guid tenantId, PaymentProvider provider)
        => context.SettlementReports
            .Where(r => r.TenantId == tenantId && r.Provider == provider)
            .OrderByDescending(r => r.PeriodEnd)
            .FirstOrDefaultAsync();

    public Task<List<SettlementReport>> GetByTenantAsync(Guid tenantId, int limit = 20, int offset = 0)
        => context.SettlementReports
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.ImportedAt)
            .Skip(offset).Take(limit)
            .ToListAsync();

    public Task<List<SettlementReportItem>> GetItemsByReportAsync(Guid reportId)
        => context.SettlementReportItems
            .Where(i => i.ReportId == reportId)
            .ToListAsync();

    public Task<List<SettlementReportItem>> GetUnmatchedItemsAsync(Guid reportId)
        => context.SettlementReportItems
            .Where(i => i.ReportId == reportId && i.MatchStatus != SettlementItemMatchStatus.MATCHED)
            .ToListAsync();

    public Task SaveChangesAsync() => context.SaveChangesAsync();
}
