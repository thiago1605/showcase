using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class FiscalInvoiceRepository(AppDbContext context) : IFiscalInvoiceRepository
{
    public async Task<FiscalInvoice?> GetByIdAsync(Guid tenantId, Guid id)
    {
        return await context.FiscalInvoices
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.Id == id);
    }

    public async Task<FiscalInvoice?> GetByTransactionIdAsync(Guid tenantId, Guid transactionId)
    {
        return await context.FiscalInvoices
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.TransactionId == transactionId);
    }

    public async Task<List<FiscalInvoice>> GetBySellerAsync(Guid tenantId, Guid sellerId, int limit = 50, int offset = 0)
    {
        return await context.FiscalInvoices
            .Where(f => f.TenantId == tenantId && f.SellerId == sellerId)
            .OrderByDescending(f => f.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<FiscalInvoice>> GetPendingRetryAsync(int limit = 50)
    {
        return await context.FiscalInvoices
            .Where(f => f.Status == FiscalInvoiceStatus.PENDING || f.Status == FiscalInvoiceStatus.FAILED_RETRYABLE)
            .Where(f => f.RetryCount < 5)
            .OrderBy(f => f.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task AddAsync(FiscalInvoice invoice)
    {
        await context.FiscalInvoices.AddAsync(invoice);
    }
}
