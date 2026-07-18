using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class PaymentIntentRepository(AppDbContext _context) : IPaymentIntentRepository
{
    public async Task<PaymentIntent?> GetByExternalReferenceAsync(Guid tenantId, string externalReferenceId)
        => await _context.Set<PaymentIntent>()
            .FirstOrDefaultAsync(pi => pi.TenantId == tenantId && pi.ExternalReferenceId == externalReferenceId);

    public async Task<PaymentIntent?> GetByIdAsync(Guid id)
        => await _context.Set<PaymentIntent>().FindAsync(id);

    public async Task<PaymentIntent?> GetByIdAsync(Guid tenantId, Guid id)
        => await _context.Set<PaymentIntent>()
            .FirstOrDefaultAsync(pi => pi.TenantId == tenantId && pi.Id == id);

    public void Add(PaymentIntent intent) => _context.Set<PaymentIntent>().Add(intent);

    public void Update(PaymentIntent intent) => _context.Set<PaymentIntent>().Update(intent);

    public async Task SaveChangesAsync() => await _context.SaveChangesAsync();

    public async Task<bool> TryCaptureAsync(Guid intentId, Guid transactionId)
    {
        // Atomic compare-and-swap: only succeed if CapturedTransactionId is still NULL
        var rows = await _context.Set<PaymentIntent>()
            .Where(pi => pi.Id == intentId && pi.CapturedTransactionId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(pi => pi.CapturedTransactionId, transactionId)
                .SetProperty(pi => pi.Status, PaymentIntentStatus.CAPTURED)
                .SetProperty(pi => pi.UpdatedAt, DateTime.UtcNow));

        return rows > 0;
    }
}
