using FellowCore.Application.Modules.Marketplace.DTOs;

namespace FellowCore.Application.Modules.Marketplace.Interfaces;

/// <summary>
/// CRUD de order bumps (ofertas adicionais no checkout). Sempre seller-scoped
/// pelo painel — só o owner do mainProduct pode configurar bumps dele.
///
/// O endpoint público (checkout) tem método separado <c>ListPublicForSlugAsync</c>
/// que filtra bumps inativos + bumps cujo bumpProduct não está PUBLISHED.
/// </summary>
public interface IProductOrderBumpService
{
    /// <summary>Lista bumps configurados pra um main product (painel do produtor). Inclui ativos+inativos.</summary>
    Task<IReadOnlyList<OrderBumpDto>> ListAsync(Guid tenantId, Guid ownerSellerId, Guid mainProductId);

    /// <summary>
    /// Cria um order bump pro main product. Valida:
    ///  - main product existe e pertence ao seller
    ///  - bump product existe, é do mesmo TenantId e mesmo OwnerSellerId
    ///  - bump != main (sem self-reference)
    ///  - max 3 bumps ATIVOS por main product
    ///  - bump não está duplicado (mesmo BumpProductId já ativo no mesmo Main)
    /// Se <c>DisplayOrder</c> não vier, atribui o próximo slot disponível (count atual).
    /// </summary>
    Task<OrderBumpDto> CreateAsync(Guid tenantId, Guid ownerSellerId, Guid mainProductId, CreateOrderBumpDto request);

    /// <summary>
    /// Atualiza campos editáveis (CustomTitle, CustomDescription, DisplayOrder, IsActive).
    /// Ao reativar (IsActive=true), revalida o limite de 3 ativos.
    /// </summary>
    Task<OrderBumpDto> UpdateAsync(Guid tenantId, Guid ownerSellerId, Guid mainProductId, Guid bumpId, UpdateOrderBumpDto request);

    /// <summary>Remove permanente. Para "esconder temporariamente", use UpdateAsync com IsActive=false.</summary>
    Task DeleteAsync(Guid tenantId, Guid ownerSellerId, Guid mainProductId, Guid bumpId);

    /// <summary>
    /// Lista pública (anônimo) — usada pelo checkout `/p/{slug}`. Resolve o
    /// mainProduct pelo slug, retorna só bumps ATIVOS cujo bumpProduct está
    /// PUBLISHED (status check evita oferecer item indisponível). Retorna null
    /// se o slug não resolve em produto publicado.
    /// </summary>
    Task<IReadOnlyList<PublicOrderBumpDto>?> ListPublicForSlugAsync(string slug);
}
