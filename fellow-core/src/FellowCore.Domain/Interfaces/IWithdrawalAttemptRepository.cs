using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IWithdrawalAttemptRepository
{
    Task<WithdrawalAttempt?> GetByIdAsync(Guid tenantId, Guid attemptId);
    Task<WithdrawalAttempt?> GetByIdempotencyKeyAsync(string idempotencyKey);
    /// <summary>
    /// Attempts em PENDING ou IN_PROGRESS — pra resume após crash. Limitado
    /// pra batch processable. Inclui Steps via Include.
    /// </summary>
    Task<List<WithdrawalAttempt>> GetUnfinishedAsync(int limit = 50);
    void Add(WithdrawalAttempt attempt);
    void Update(WithdrawalAttempt attempt);
    Task SaveChangesAsync();
}
