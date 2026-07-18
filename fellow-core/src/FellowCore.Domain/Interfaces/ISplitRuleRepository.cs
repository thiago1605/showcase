using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ISplitRuleRepository
{
    void Add(SplitRule rule);
    void Update(SplitRule rule);
    Task<SplitRule?> GetByIdAsync(Guid tenantId, Guid id);
    Task<SplitRule?> GetByIdWithRecipientsAsync(Guid tenantId, Guid id);
    Task<(IReadOnlyList<SplitRule> Items, int TotalCount)> GetPagedAsync(Guid tenantId, int skip, int take);
    /// <summary>
    /// Same as GetPagedAsync but returns only rules where the given seller is one of the
    /// recipients. Used by the seller portal so the list shows only rules that affect the
    /// authenticated seller.
    /// </summary>
    Task<(IReadOnlyList<SplitRule> Items, int TotalCount)> GetPagedByRecipientAsync(Guid tenantId, Guid sellerId, int skip, int take);

    /// <summary>
    /// Returns rules where the seller is owner OR appears as a recipient. The portal uses
    /// this so a seller sees rules they created and rules where they receive a share.
    /// </summary>
    Task<(IReadOnlyList<SplitRule> Items, int TotalCount)> GetPagedByOwnerOrRecipientAsync(Guid tenantId, Guid sellerId, int skip, int take);
    Task<bool> ExistsByNameAsync(Guid tenantId, string name);
    Task SaveChangesAsync();
}
