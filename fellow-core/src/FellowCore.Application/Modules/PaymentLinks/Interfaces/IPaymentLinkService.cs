using FellowCore.Application.Modules.PaymentLinks.DTOs;
using FellowCore.Application.Modules.Transactions.DTOs;

namespace FellowCore.Application.Modules.PaymentLinks.Interfaces;

public interface IPaymentLinkService
{
    Task<PaymentLinkResponseDto> CreateAsync(Guid tenantId, CreatePaymentLinkDto request, string baseUrl);
    Task<IEnumerable<PaymentLinkResponseDto>> ListAsync(Guid tenantId, string baseUrl, Guid? sellerId = null);
    Task<PaymentLinkResponseDto> GetByIdAsync(Guid tenantId, Guid id, string baseUrl);
    Task DeactivateAsync(Guid tenantId, Guid id);
    Task<PaymentLinkResponseDto> UpdateAsync(Guid tenantId, Guid id, UpdatePaymentLinkDto request, string baseUrl);
    Task<PaymentLinkResolveDto> ResolveAsync(string token);
    Task<TransactionResponseDto> PayAsync(string token, PayPaymentLinkDto request);

    /// <summary>
    /// Public anonymous status check for a transaction created from this link.
    /// Used by the public checkout to poll Pix status (and any other long-running
    /// rail) without needing auth. Validates that the transaction belongs to the
    /// link's tenant before returning anything.
    /// </summary>
    Task<PaymentLinkTransactionStatusDto> GetTransactionStatusAsync(string token, Guid transactionId);

    /// <summary>
    /// Opções de parcelamento disponíveis pro link, "sem juros" (seller absorve).
    /// Retorna lista vazia quando o link não aceita CREDIT_CARD ou o seller é null.
    /// Endpoint anônimo — só expõe count + valor por parcela (sem fees internos).
    /// </summary>
    Task<IReadOnlyList<PaymentLinkInstallmentOptionDto>> GetInstallmentOptionsAsync(string token);
}

// Public, anonymous payload for /payment-links/pay/{token}. Carries only fields that
// are safe to expose to a paying customer — never CNPJ, internal IDs, payout config,
// or contact details. SellerName resolves from Seller.TradeName ?? Seller.LegalName.
public record PaymentLinkResolveDto(
    decimal Amount,
    // Método "primário"/default. Mantido pra backward-compat com clients antigos.
    string PaymentType,
    // Lista de métodos aceitos. Sempre ≥1. Quando >1, o checkout mostra um seletor.
    string[] PaymentTypes,
    int Installments,
    string? Description,
    string? SellerName);

// Public anonymous polling response. Status is the enum name (e.g. CAPTURED).
// IsTerminal lets the frontend stop polling without re-deriving from a string list.
public record PaymentLinkTransactionStatusDto(string Status, bool IsTerminal);

/// <summary>
/// Opção de parcelamento exposta ao checkout público. Não inclui breakdown
/// de fees internos (platform/provider) — só o que o comprador precisa ver.
/// Modo "sem juros": <c>total = amount</c>, comprador paga o mesmo independente
/// do N escolhido.
/// </summary>
public record PaymentLinkInstallmentOptionDto(
    int Count,
    decimal PerInstallmentAmount,
    decimal Total);
