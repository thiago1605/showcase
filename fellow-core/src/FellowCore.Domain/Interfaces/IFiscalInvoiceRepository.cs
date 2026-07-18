using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IFiscalInvoiceRepository
{
    Task<FiscalInvoice?> GetByIdAsync(Guid tenantId, Guid id);
    Task<FiscalInvoice?> GetByTransactionIdAsync(Guid tenantId, Guid transactionId);
    Task<List<FiscalInvoice>> GetBySellerAsync(Guid tenantId, Guid sellerId, int limit = 50, int offset = 0);
    Task<List<FiscalInvoice>> GetPendingRetryAsync(int limit = 50);
    Task AddAsync(FiscalInvoice invoice);
}
