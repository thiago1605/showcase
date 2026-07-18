using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.Subscriptions.DTOs;

namespace FellowCore.Application.Modules.Subscriptions.Interfaces;

public interface ISubscriptionService
{
    Task<SubscriptionResponseDto> CreateAsync(Guid tenantId, CreateSubscriptionDto request);
    Task<SubscriptionDetailDto> GetByIdAsync(Guid tenantId, Guid id);
    Task<PagedResult<SubscriptionDetailDto>> ListAsync(Guid tenantId, SubscriptionFilterDto filter);
    Task<SubscriptionDetailDto> CancelAsync(Guid tenantId, Guid id);
    Task<SubscriptionDetailDto> PauseAsync(Guid tenantId, Guid id);
    Task<SubscriptionDetailDto> ResumeAsync(Guid tenantId, Guid id);
}
