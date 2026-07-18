using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class LedgerRepository(AppDbContext _context) : ILedgerRepository
{
    public void AddAccount(LedgerAccount account)
    {
        _context.LedgerAccounts.Add(account);
    }

    public void UpdateAccount(LedgerAccount account)
    {
        _context.LedgerAccounts.Update(account);
    }

    public void AddEntry(LedgerEntry entry)
    {
        _context.LedgerEntries.Add(entry);
    }

    public Task<LedgerAccount?> GetAccountAsync(Guid tenantId, LedgerAccountType type, Guid sellerId, PaymentProvider? provider = null)
    {
        // Provider null = comportamento legado (qualquer conta do seller+type).
        // Provider explícito = filtro adicional pra multi-provider.
        var query = _context.LedgerAccounts
            .Where(la => la.TenantId == tenantId && la.Type == type && la.SellerId == sellerId);
        if (provider.HasValue)
            query = query.Where(la => la.Provider == provider.Value);
        return query.FirstOrDefaultAsync();
    }

    public Task<LedgerAccount?> GetPlatformAccountAsync(Guid tenantId, LedgerAccountType type)
    {
        return _context.LedgerAccounts.FirstOrDefaultAsync(ledgerAccount => ledgerAccount.TenantId == tenantId && ledgerAccount.Type == type && ledgerAccount.SellerId == null);
    }

    public async Task<List<LedgerAccount>> GetAccountsBySellerAsync(Guid tenantId, Guid sellerId)
    {
        return await _context.LedgerAccounts.Where(ledgerAccount => ledgerAccount.TenantId == tenantId && ledgerAccount.SellerId == sellerId).ToListAsync();
    }

    public async Task<List<LedgerAccount>> GetSellerAccountsByTypeAsync(Guid tenantId, Guid sellerId, LedgerAccountType type)
    {
        return await _context.LedgerAccounts
            .Where(la => la.TenantId == tenantId && la.SellerId == sellerId && la.Type == type)
            .OrderBy(la => la.Provider)
            .ToListAsync();
    }

    public async Task<List<LedgerAccountSummary>> GetAccountsWithEntryTotalsAsync()
    {
        return await _context.LedgerAccounts
            .AsNoTracking()
            .Select(a => new LedgerAccountSummary(
                a.Id,
                a.TenantId,
                a.SellerId,
                a.Type,
                a.Balance,
                a.Entries.Sum(e => e.Amount)))
            .ToListAsync();
    }

    public async Task<List<LedgerAccountSummary>> GetAccountsWithEntryTotalsAsync(Guid tenantId)
    {
        return await _context.LedgerAccounts
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Select(a => new LedgerAccountSummary(
                a.Id,
                a.TenantId,
                a.SellerId,
                a.Type,
                a.Balance,
                a.Entries.Sum(e => e.Amount)))
            .ToListAsync();
    }

    public async Task<bool> HasEntryWithReferenceAsync(Guid tenantId, string referenceType, string referenceId)
    {
        return await _context.LedgerEntries
            .AnyAsync(e => e.Account.TenantId == tenantId && e.ReferenceType == referenceType && e.ReferenceId == referenceId);
    }

    public async Task<List<LedgerAccount>> GetNegativeWalletAccountsAsync(Guid tenantId)
    {
        return await _context.LedgerAccounts
            .Where(a => a.TenantId == tenantId && a.Type == LedgerAccountType.WALLET && a.Balance < 0)
            .ToListAsync();
    }

    public async Task<LedgerEntry?> GetLatestEntryAsync(Guid accountId)
    {
        return await _context.LedgerEntries
            .Where(e => e.AccountId == accountId)
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<int> GetDuplicateIdempotencyKeyCountAsync(Guid tenantId, string referenceType)
    {
        // Count reference keys that appear on more entries than expected (>2 means duplicate pair)
        return await _context.LedgerEntries
            .Where(e => e.Account.TenantId == tenantId && e.ReferenceType == referenceType)
            .GroupBy(e => e.ReferenceId)
            .Where(g => g.Count() > 2) // Each operation creates exactly 2 entries (debit+credit)
            .CountAsync();
    }

    public async Task SaveChangesAsync()
    {
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            DetachAllEntities();
            throw new ConcurrencyException("LedgerAccount.ConcurrencyConflict", "Conflito de concorrência ao atualizar conta do ledger. Tente novamente.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Unique constraint violation on concurrent account creation — treat as retryable
            DetachAllEntities();
            throw new ConcurrencyException("LedgerAccount.ConcurrencyConflict", "Conta do ledger criada concorrentemente. Tente novamente.");
        }
    }

    private void DetachAllEntities()
    {
        _context.ChangeTracker.Clear();
    }

    public async Task ReloadAsync(LedgerAccount account)
    {
        await _context.Entry(account).ReloadAsync();
    }
}