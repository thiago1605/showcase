using FellowCore.Application.Common.Models;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Customers.DTOs;
using FellowCore.Application.Modules.Customers.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.Customers.Services;

public class CustomerService(ICustomerRepository customerRepository) : ICustomerService
{
    public async Task<CustomerResponseDto> CreateAsync(Guid tenantId, CreateCustomerDto request)
    {
        var existing = await customerRepository.GetByEmailAsync(tenantId, request.Email);
        if (existing != null)
            throw new ConflictException("Customer.EmailExists", $"Já existe um cliente com o email {request.Email}.");

        var customer = Customer.Create(tenantId, request.Name, request.Email, request.Document, request.ExternalId);

        customerRepository.Add(customer);
        await customerRepository.SaveChangesAsync();

        return MapToResponse(customer);
    }

    public async Task<CustomerDetailDto> GetByIdAsync(Guid tenantId, Guid id)
    {
        var customer = await customerRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("Customer.NotFound", $"Cliente {id} não encontrado.");

        return new CustomerDetailDto(
            customer.Id, customer.TenantId, customer.Name, customer.Email,
            customer.Document, customer.ExternalId, customer.CreatedAt,
            customer.PaymentMethods.Any()
                ? customer.PaymentMethods.Select(MapPaymentMethod).ToList()
                : null);
    }

    public async Task<PagedResult<CustomerResponseDto>> ListAsync(Guid tenantId, int page, int pageSize)
    {
        var (skip, take, normalizedPage) = PagedResult<CustomerResponseDto>.Normalize(page, pageSize);
        var (items, totalCount) = await customerRepository.GetPagedAsync(tenantId, skip, take);

        return new PagedResult<CustomerResponseDto>(
            Items: items.Select(MapToResponse).ToList(),
            TotalCount: totalCount,
            Page: normalizedPage,
            PageSize: take);
    }

    public async Task<PaymentMethodDto> AddPaymentMethodAsync(Guid tenantId, Guid customerId, AddPaymentMethodDto request)
    {
        var customer = await customerRepository.GetByIdAsync(tenantId, customerId)
            ?? throw new NotFoundException("Customer.NotFound", $"Cliente {customerId} não encontrado.");

        var pm = customer.AddPaymentMethod(
            request.Type, request.Token, request.Gateway,
            request.First6, request.Last4, request.Brand,
            request.Expiration, request.HolderName,
            request.Fingerprint, request.IsDefault);

        await customerRepository.SaveChangesAsync();

        return MapPaymentMethod(pm);
    }

    public async Task<CustomerResponseDto> UpdateAsync(Guid tenantId, Guid customerId, UpdateCustomerDto request)
    {
        var customer = await customerRepository.GetByIdAsync(tenantId, customerId)
            ?? throw new NotFoundException("Customer.NotFound", $"Cliente {customerId} nao encontrado.");

        if (request.Email != null && request.Email != customer.Email)
        {
            var existing = await customerRepository.GetByEmailAsync(tenantId, request.Email);
            if (existing != null && existing.Id != customerId)
                throw new ConflictException("Customer.EmailExists", $"Ja existe um cliente com o email {request.Email}.");
        }

        customer.Update(request.Name, request.Email, request.Document);

        customerRepository.Update(customer);
        await customerRepository.SaveChangesAsync();

        return MapToResponse(customer);
    }

    private static CustomerResponseDto MapToResponse(Customer c) =>
        new(c.Id, c.Name, c.Email, c.Document, c.ExternalId, c.CreatedAt);

    private static PaymentMethodDto MapPaymentMethod(PaymentMethod pm) =>
        new(pm.Id, pm.Type, pm.Card?.Last4, pm.Card?.Brand,
            pm.Card?.HolderName, pm.Card?.Expiration, pm.IsDefault, pm.CreatedAt);
}
