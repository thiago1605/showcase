using FellowCore.Application.Modules.Splits.DTOs;

namespace FellowCore.Application.Modules.Splits.Interfaces;

public interface IItemSplitResolver
{
    Task<ItemSplitResolution> ResolveFromItemsAsync(Guid tenantId, Guid transactionId);
}

public record ItemSplitResolution(
    List<AggregatedRecipient> Recipients,
    bool HasItemSplits
);

public record AggregatedRecipient(
    Guid SellerId,
    decimal Amount,
    string Source // "ITEM_RULE", "ITEM_EXPLICIT"
);
