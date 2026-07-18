using FellowCore.Application.Modules.Sellers.DTOs;

namespace FellowCore.Application.Modules.Sellers.Interfaces;

/// <summary>
/// Sprint 0: serviço de leitura on-the-fly do tier de performance do seller.
/// Não persiste, não dispara evento, não tem cooldown. Pensado pra alimentar o
/// portal sem custo de migração — Sprint 1 substituirá por job persistido.
/// </summary>
public interface ISellerTierService
{
    Task<SellerTierDto> GetTierAsync(Guid tenantId, Guid sellerId);
}
