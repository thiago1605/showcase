using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.Customers.DTOs;

public record CreateCustomerDto(
    string Name,
    string Email,
    string? Document = null,
    string? ExternalId = null);

public record UpdateCustomerDto(
    string? Name = null,
    string? Email = null,
    string? Document = null
);

public record CustomerResponseDto(
    Guid Id,
    string Name,
    string Email,
    string? Document,
    string? ExternalId,
    DateTime CreatedAt);

public record CustomerDetailDto(
    Guid Id,
    Guid TenantId,
    string Name,
    string Email,
    string? Document,
    string? ExternalId,
    DateTime CreatedAt,
    IReadOnlyList<PaymentMethodDto>? PaymentMethods);

public record PaymentMethodDto(
    Guid Id,
    PaymentType Type,
    string? Last4,
    string? Brand,
    string? HolderName,
    string? Expiration,
    bool IsDefault,
    DateTime CreatedAt);

public record AddPaymentMethodDto(
    PaymentType Type,
    string Token,
    PaymentProvider Gateway,
    string First6,
    string Last4,
    string Brand,
    string Expiration,
    string HolderName,
    string? Fingerprint = null,
    bool IsDefault = false);
