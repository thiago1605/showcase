namespace FellowCore.Application.Modules.PixPayments.DTOs;

public record ValidatePixKeyRequest(string PixKey);
public record CreateStaticQrRequest(string Name, decimal? Amount = null, string? Description = null);

public record CreatePixPaymentDto(
    string DestinationPixKey,
    decimal Amount,
    string? Description = null
);

public record PixPaymentResponseDto(
    Guid Id,
    string CorrelationId,
    string DestinationPixKey,
    decimal Amount,
    string Status,
    string? Description,
    DateTime CreatedAt
);

public record PixPaymentDetailDto(
    Guid Id,
    string CorrelationId,
    string DestinationPixKey,
    decimal Amount,
    string Status,
    string? ProviderTransactionId,
    string? Description,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
