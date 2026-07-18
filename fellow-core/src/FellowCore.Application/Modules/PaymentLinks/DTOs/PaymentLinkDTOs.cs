using FellowCore.Domain.Enums;

namespace FellowCore.Application.Modules.PaymentLinks.DTOs;

public record CreatePaymentLinkDto(
    decimal Amount,
    PaymentType PaymentType,
    int Installments = 1,
    Guid? SellerId = null,
    string? Description = null,
    // null/omitido = ilimitado; positivo = teto exato.
    int? MaxUses = null,
    DateTime? ExpiresAt = null,
    Guid? SplitRuleId = null,
    // Quando set, link aceita qualquer um destes métodos (cliente escolhe no
    // checkout). PaymentType acima é usado como default e pra backward-compat.
    // Quando omitido, link é restrito a PaymentType (modo legacy single).
    PaymentType[]? PaymentTypes = null,
    // Modelo Híbrido: override per-link da antecipação automática. null = herda
    // seller.AutoAdvanceSettlement; true = força ADVANCE; false = força INSTALLMENT.
    bool? AdvanceOptIn = null);

public record PaymentLinkResponseDto(
    Guid Id,
    string Token,
    string Url,
    decimal Amount,
    PaymentType PaymentType,
    // Sempre tem ≥1 elemento. Para links legacy = [PaymentType]. Frontend admin
    // exibe como tags/checkboxes; checkout público usa pra renderizar o seletor.
    PaymentType[] PaymentTypes,
    int Installments,
    string? Description,
    // null = ilimitado; frontend renderiza como ∞.
    int? MaxUses,
    int UsageCount,
    bool Active,
    DateTime? ExpiresAt,
    DateTime CreatedAt,
    Guid? SellerId,
    Guid? SplitRuleId,
    /// <summary>
    /// Override per-link da antecipação automática. null = inherit (TX herda do seller);
    /// true/false = force.
    /// </summary>
    bool? AdvanceOptIn = null);

// Card-typed payment links accept an empty body so the frontend can fetch the Stripe
// clientSecret eagerly and render wallets-first (Apple/Google Pay/Link). Pix and Boleto
// links still require name + document + email — that's enforced at the service layer.
public record PayPaymentLinkDto(
    string? PayerName = null,
    string? PayerDocument = null,
    string? PayerEmail = null,
    string? PayerPhone = null,
    // Em links multi-método, o cliente escolhe qual método usar no checkout.
    // Quando omitido em link legacy single, usamos PaymentLink.PaymentType.
    // Quando omitido em link multi-método, falha com 422.
    PaymentType? ChosenPaymentType = null,
    // Parcelas escolhidas pelo cliente no checkout (modo sem juros, seller absorve).
    // Quando null, usa link.Installments. Validado server-side contra o cap efetivo
    // do seller — se vier acima, a TX cria com o cap (clamp silencioso, evita 422).
    int? ChosenInstallments = null);

// Edit payload com semântica PUT-like: o frontend envia o estado completo dos campos
// editáveis. `null` em qualquer um significa "remover" (sem cap, sem expiração, sem
// regra de split). Amount e PaymentType NÃO são editáveis — alterá-los quebraria
// snapshots de transações já criadas via esse link.
public record UpdatePaymentLinkDto(
    string? Description,
    int? MaxUses,
    DateTime? ExpiresAt,
    Guid? SplitRuleId,
    // Quando set, atualiza a lista de métodos aceitos. Vazio/omitido mantém o
    // estado atual.
    PaymentType[]? PaymentTypes = null,
    // Override per-link da antecipação automática (Modelo Híbrido).
    // null = manter estado atual; usar AdvanceOptInReset=true pra resetar pra inherit.
    bool? AdvanceOptIn = null,
    // Quando true E AdvanceOptIn=null, volta pro modo inherit (TX herda do seller).
    // Padrão false: omitir AdvanceOptIn mantém valor atual.
    bool AdvanceOptInReset = false);
