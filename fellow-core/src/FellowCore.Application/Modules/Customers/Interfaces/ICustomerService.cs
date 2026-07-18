using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.Customers.DTOs;

namespace FellowCore.Application.Modules.Customers.Interfaces;

public interface ICustomerService
{
    Task<CustomerResponseDto> CreateAsync(Guid tenantId, CreateCustomerDto request);
    Task<CustomerDetailDto> GetByIdAsync(Guid tenantId, Guid id);
    Task<PagedResult<CustomerResponseDto>> ListAsync(Guid tenantId, int page, int pageSize);
    Task<PaymentMethodDto> AddPaymentMethodAsync(Guid tenantId, Guid customerId, AddPaymentMethodDto request);
    Task<CustomerResponseDto> UpdateAsync(Guid tenantId, Guid customerId, UpdateCustomerDto request);
}
