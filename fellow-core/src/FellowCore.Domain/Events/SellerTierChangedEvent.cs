using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Primitives;

namespace FellowCore.Domain.Events;

/// <summary>
/// Disparado pelo job mensal de recalculo de tier quando um seller efetivamente
/// muda de tier (não disparado para <see cref="SellerTierTransition.Unchanged"/>
/// nem <see cref="SellerTierTransition.BlockedByFreeze"/>).
///
/// Consumidores típicos:
///   - Notificação ao seller ("Parabéns, você subiu pra Gold — desconto X% na próxima TX")
///   - Webhook outbound (`seller.tier.changed`) pra integrações
///   - Métrica Prometheus por transição (Sprint 1 #3)
///   - Reset de caches de preço por seller
/// </summary>
public sealed record SellerTierChangedEvent(
    Guid SellerId,
    Guid TenantId,
    SellerTier PreviousTier,
    SellerTier NewTier,
    SellerTierTransition Transition,
    decimal Tpv90dSnapshotBrl,
    DateTime OccurredAt) : IDomainEvent
{
    public SellerTierChangedEvent(
        Guid sellerId,
        Guid tenantId,
        SellerTier previousTier,
        SellerTier newTier,
        SellerTierTransition transition,
        decimal tpv90dSnapshotBrl)
        : this(sellerId, tenantId, previousTier, newTier, transition, tpv90dSnapshotBrl, DateTime.UtcNow) { }
}
