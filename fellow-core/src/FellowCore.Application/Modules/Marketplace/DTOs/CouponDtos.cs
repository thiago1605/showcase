using System.ComponentModel.DataAnnotations;
using FellowCore.Domain.Entities;

namespace FellowCore.Application.Modules.Marketplace.DTOs;

public record CouponDto(
    Guid Id,
    Guid? ProductId,
    /// <summary>Nome do produto quando ProductId != null. Preenchido pelo
    /// service quando lista; null em cupons globais (sem produto). Frontend
    /// usa pra exibir "Global" vs "Produto X" na lista.</summary>
    string? ProductName,
    string Code,
    int Type,                  // 0=PERCENT, 1=FIXED
    decimal Value,
    DateTime? ValidFrom,
    DateTime? ValidUntil,
    int? MaxUses,
    int UsedCount,
    DateTime CreatedAt
);

public record CreateCouponDto(
    [Required, MaxLength(32)] string Code,
    [Required] CouponType Type,
    [Required, Range(0.01, double.MaxValue)] decimal Value,
    /// <summary>Null = cupom global do produtor; non-null = restrito ao produto.</summary>
    Guid? ProductId = null,
    DateTime? ValidFrom = null,
    DateTime? ValidUntil = null,
    [Range(1, int.MaxValue)] int? MaxUses = null
);

/// <summary>
/// Resposta da validação pública de cupom — informa se é válido + quanto
/// desconta sobre o preço do produto.
/// </summary>
public record CouponValidationDto(
    string Code,
    int Type,
    decimal Value,
    /// <summary>Valor calculado do desconto no preço atual do produto.</summary>
    decimal DiscountAmount,
    /// <summary>Preço final após o desconto (= price - discount).</summary>
    decimal FinalPrice
);
