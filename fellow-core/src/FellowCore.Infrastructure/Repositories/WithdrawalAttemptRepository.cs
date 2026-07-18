using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class WithdrawalAttemptRepository(AppDbContext _context) : IWithdrawalAttemptRepository
{
    public async Task<WithdrawalAttempt?> GetByIdAsync(Guid tenantId, Guid attemptId)
        => await _context.WithdrawalAttempts
            .Include(a => a.Steps.OrderBy(s => s.Sequence))
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == attemptId);

    public async Task<WithdrawalAttempt?> GetByIdempotencyKeyAsync(string idempotencyKey)
        => await _context.WithdrawalAttempts
            .Include(a => a.Steps.OrderBy(s => s.Sequence))
            .FirstOrDefaultAsync(a => a.IdempotencyKey == idempotencyKey);

    public async Task<List<WithdrawalAttempt>> GetUnfinishedAsync(int limit = 50)
        => await _context.WithdrawalAttempts
            .Include(a => a.Steps.OrderBy(s => s.Sequence))
            .Where(a => a.Status == WithdrawalAttemptStatus.PENDING
                     || a.Status == WithdrawalAttemptStatus.IN_PROGRESS)
            .OrderBy(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync();

    public void Add(WithdrawalAttempt attempt) => _context.WithdrawalAttempts.Add(attempt);
    public void Update(WithdrawalAttempt attempt) => _context.WithdrawalAttempts.Update(attempt);
    public async Task SaveChangesAsync() => await _context.SaveChangesAsync();
}
