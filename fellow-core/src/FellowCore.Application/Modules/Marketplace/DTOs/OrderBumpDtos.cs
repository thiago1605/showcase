using System.ComponentModel.DataAnnotations;

namespace FellowCore.Application.Modules.Marketplace.DTOs;

/// <summary>
/// Representação interna (painel do produtor) — inclui ids + dados de display
/// do produto referenciado (nome, preço, cover) join-ado pelo repository
/// pra evitar N+1 no frontend.
/// </summary>
public record OrderBumpDto(
    Guid Id,
    Guid MainProductId,
    Guid BumpProductId,
    string BumpProductName,
    decimal BumpProductPrice,
    string? BumpProductCoverImageUrl,
    /// <summary>Status do produto referenciado — frontend exibe alerta se != PUBLISHED.</summary>
    int BumpProductStatus,
    string CustomTitle,
    string? CustomDescription,
    int DisplayOrder,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>Desconto absoluto (R$) aplicado ao bump no checkout. 0 = sem desconto.</summary>
    decimal DiscountAmount = 0m
);

public record CreateOrderBumpDto(
    [Required] Guid BumpProductId,
    [Required, MaxLength(200)] string CustomTitle,
    [MaxLength(500)] string? CustomDescription = null,
    int? DisplayOrder = null,
    [Range(0, 999999.99, ErrorMessage = "Desconto inválido.")]
    decimal? DiscountAmount = null
);

public record UpdateOrderBumpDto(
    [MaxLength(200)] string? CustomTitle = null,
    [MaxLength(500)] string? CustomDescription = null,
    int? DisplayOrder = null,
    bool? IsActive = null,
    [Range(0, 999999.99, ErrorMessage = "Desconto inválido.")]
    decimal? DiscountAmount = null
);

/// <summary>
/// Representação pública (checkout anônimo) — sem ids internos sensíveis,
/// só o suficiente pra renderizar o card do bump e validar a seleção.
/// </summary>
public record PublicOrderBumpDto(
    Guid Id,
    Guid BumpProductId,
    string Title,
    string? Description,
    /// <summary>Preço original do produto bump (referência para mostrar strikethrough). </summary>
    decimal Price,
    string Currency,
    string? CoverImageUrl,
    int DisplayOrder,
    /// <summary>Desconto absoluto a aplicar no checkout (R$). 0 = sem desconto.</summary>
    decimal DiscountAmount = 0m,
    /// <summary>Preço final cobrado se o cliente marcar o bump = Price - DiscountAmount.</summary>
    decimal FinalPrice = 0m
);
