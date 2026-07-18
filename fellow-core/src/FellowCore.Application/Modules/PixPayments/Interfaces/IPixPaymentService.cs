using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.PixPayments.DTOs;

namespace FellowCore.Application.Modules.PixPayments.Interfaces;

public interface IPixPaymentService
{
    Task<PixPaymentResponseDto> CreateAsync(Guid tenantId, CreatePixPaymentDto request);
    Task<PixPaymentDetailDto> GetByIdAsync(Guid tenantId, Guid id);
    Task<PagedResult<PixPaymentDetailDto>> ListAsync(Guid tenantId, int page, int pageSize);
}
