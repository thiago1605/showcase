using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.Splits.DTOs;

namespace FellowCore.Application.Modules.Splits.Interfaces;

public interface ISplitRuleService
{
    Task<SplitRuleResponseDto> CreateAsync(Guid tenantId, CreateSplitRuleDto request, Guid? ownerSellerId = null);
    Task<SplitRuleResponseDto> GetByIdAsync(Guid tenantId, Guid id);
    /// <summary>
    /// Lists split rules. Pass `ownerOrRecipientSellerId` to scope the result to rules
    /// where the seller is owner OR appears as a recipient (seller portal). Pass null
    /// for the tenant-wide listing (API key / platform operator).
    /// </summary>
    Task<PagedResult<SplitRuleResponseDto>> ListAsync(Guid tenantId, int page, int pageSize, Guid? ownerOrRecipientSellerId = null);
    Task ActivateAsync(Guid tenantId, Guid id);
    Task DeactivateAsync(Guid tenantId, Guid id);
}
