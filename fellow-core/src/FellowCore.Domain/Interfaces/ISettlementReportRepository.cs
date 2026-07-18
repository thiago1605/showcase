using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Domain.Interfaces;

public interface ISettlementReportRepository
{
    void Add(SettlementReport report);
    void AddItem(SettlementReportItem item);
    void AddItems(IEnumerable<SettlementReportItem> items);
    Task<SettlementReport?> GetByIdAsync(Guid tenantId, Guid reportId);
    Task<SettlementReport?> GetLatestAsync(Guid tenantId, PaymentProvider provider);
    Task<List<SettlementReport>> GetByTenantAsync(Guid tenantId, int limit = 20, int offset = 0);
    Task<List<SettlementReportItem>> GetItemsByReportAsync(Guid reportId);
    Task<List<SettlementReportItem>> GetUnmatchedItemsAsync(Guid reportId);
    Task SaveChangesAsync();
}
