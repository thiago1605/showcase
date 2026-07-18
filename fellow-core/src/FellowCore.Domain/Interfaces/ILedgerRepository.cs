using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Ledgers.Interfaces;

public record LedgerAccountSummary(Guid AccountId, Guid TenantId, Guid? SellerId, LedgerAccountType Type, decimal CurrentBalance, decimal SumOfEntries);

public interface ILedgerRepository
{
    /// <summary>
    /// Get account de seller — quando <c>provider</c> é null, retorna primeira
    /// match (compatibilidade com chamadas legadas). Pra multi-provider, sempre
    /// passar provider explícito.
    /// </summary>
    Task<LedgerAccount?> GetAccountAsync(Guid tenantId, LedgerAccountType type, Guid sellerId, PaymentProvider? provider = null);
    Task<LedgerAccount?> GetPlatformAccountAsync(Guid tenantId, LedgerAccountType type);
    Task<List<LedgerAccount>> GetAccountsBySellerAsync(Guid tenantId, Guid sellerId);
    /// <summary>
    /// Retorna WALLETs (e/ou FUTURE_RECEIVABLES) do seller agrupadas por provedor.
    /// Usado pelo orchestrador de saque pra decidir alocação multi-provider.
    /// </summary>
    Task<List<LedgerAccount>> GetSellerAccountsByTypeAsync(Guid tenantId, Guid sellerId, LedgerAccountType type);
    /// <summary>
    /// Returns all accounts across all tenants (used by background reconciliation batch).
    /// Prefer the tenant-scoped overload for user-facing queries.
    /// </summary>
    Task<List<LedgerAccountSummary>> GetAccountsWithEntryTotalsAsync();
    /// <summary>
    /// Returns accounts scoped to a single tenant — use this for tenant-isolated reconciliation.
    /// </summary>
    Task<List<LedgerAccountSummary>> GetAccountsWithEntryTotalsAsync(Guid tenantId);
    void AddAccount(LedgerAccount account);
    void UpdateAccount(LedgerAccount account);
    void AddEntry(LedgerEntry entry);
    Task<bool> HasEntryWithReferenceAsync(Guid tenantId, string referenceType, string referenceId);
    Task<List<LedgerAccount>> GetNegativeWalletAccountsAsync(Guid tenantId);
    /// <summary>Última entrada de uma conta — usada pra diagnosticar causa de
    /// saldo negativo (refund vs anomalia). Retorna null se a conta nunca teve
    /// movimentação (caso teórico, contas são criadas com balance=0).</summary>
    Task<LedgerEntry?> GetLatestEntryAsync(Guid accountId);
    Task<int> GetDuplicateIdempotencyKeyCountAsync(Guid tenantId, string referenceType);
    Task SaveChangesAsync();
    Task ReloadAsync(LedgerAccount account);
}