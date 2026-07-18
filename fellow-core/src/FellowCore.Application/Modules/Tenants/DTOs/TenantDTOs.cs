namespace FellowCore.Application.Modules.Tenants.DTOs;

public record CreateTenantDto
{
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? OwnerEmail { get; init; }
}

public record TenantResponse(Guid Id, string Name, string Slug, string MaskedApiKey, DateTime CreatedAt);

public record TenantCreateResponse(TenantResponse Tenant, string ApiKey, string ApiSecret);

public record RotateApiKeyDto
{
    public string CurrentApiSecret { get; init; } = string.Empty;
}

public record RotateApiKeyResponse(string ApiKey, string ApiSecret);

public record UpdateTenantProvidersDto
{
    public FellowCore.Domain.Enums.PaymentProvider? ActivePixProvider { get; init; }
    public FellowCore.Domain.Enums.PaymentProvider? ActiveCreditProvider { get; init; }
}
