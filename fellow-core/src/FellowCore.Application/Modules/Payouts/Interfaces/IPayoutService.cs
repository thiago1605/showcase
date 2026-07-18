using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.Payouts.DTOs;

namespace FellowCore.Application.Modules.Payouts.Interfaces;

public interface IPayoutService
{
    Task<PayoutResponseDto> CreateAsync(Guid tenantId, CreatePayoutDto request);
    Task RetryAsync(Guid payoutId);
    Task<PayoutDetailDto> GetByIdAsync(Guid tenantId, Guid id);
    Task<PagedResult<PayoutDetailDto>> ListAsync(Guid tenantId, PayoutFilterDto filter);
}
