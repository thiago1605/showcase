using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.PaymentLinks.DTOs;
using FellowCore.Application.Modules.PaymentLinks.Interfaces;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;

namespace FellowCore.Application.Modules.PaymentLinks.Services;

public class PaymentLinkService(
    IPaymentLinkRepository linkRepository,
    ITransactionService transactionService,
    ISplitRuleRepository splitRuleRepository,
    ISellerRepository sellerRepository,
    IPricingService pricingService,
    IConfiguration configuration) : IPaymentLinkService
{
    /// <summary>
    /// The base URL where the customer-facing checkout page lives. The link minted into
    /// PaymentLinkResponseDto.Url combines this with `/pay/{token}` so opening it lands
    /// on the portal's public checkout page, NOT on the API JSON endpoint.
    ///
    /// Reads `Frontend:BaseUrl` from configuration. When unset (e.g. tests, weird deploy),
    /// falls back to the per-request backend baseUrl as a last resort so the link still
    /// resolves SOMEWHERE — but the customer-facing experience is the portal.
    /// </summary>
    private string ResolvePublicBase(string requestBaseUrl)
    {
        var configured = configuration["Frontend:BaseUrl"];
        return string.IsNullOrWhiteSpace(configured) ? requestBaseUrl : configured.TrimEnd('/');
    }

    public async Task<PaymentLinkResponseDto> CreateAsync(Guid tenantId, CreatePaymentLinkDto request, string baseUrl)
    {
        // If a split rule is requested, it must exist in the same tenant and be active.
        // Caller-side ownership (seller can only use their own rules; API key is free) is
        // enforced in the controller before this point — here we trust the caller and
        // only validate domain invariants.
        if (request.SplitRuleId.HasValue && request.SplitRuleId.Value != Guid.Empty)
        {
            var rule = await splitRuleRepository.GetByIdAsync(tenantId, request.SplitRuleId.Value)
                ?? throw new NotFoundException("PaymentLink.SplitRuleNotFound", "Regra de split nao encontrada.");
            if (!rule.IsActive)
                throw new ValidationException("PaymentLink.SplitRuleInactive", "A regra de split selecionada esta inativa.");
        }

        var link = PaymentLink.Create(
            tenantId, request.Amount, request.PaymentType,
            request.Installments, request.SellerId, request.Description,
            request.MaxUses, request.ExpiresAt, request.SplitRuleId,
            request.PaymentTypes,
            advanceOptIn: request.AdvanceOptIn);

        linkRepository.Add(link);
        await linkRepository.SaveChangesAsync();

        return MapToResponse(link, baseUrl);
    }

    public async Task<IEnumerable<PaymentLinkResponseDto>> ListAsync(Guid tenantId, string baseUrl, Guid? sellerId = null)
    {
        var links = await linkRepository.GetByTenantAsync(tenantId, sellerId);
        return links.Select(l => MapToResponse(l, baseUrl));
    }

    public async Task<PaymentLinkResponseDto> GetByIdAsync(Guid tenantId, Guid id, string baseUrl)
    {
        var link = await linkRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("PaymentLink.NotFound", "Link de pagamento nao encontrado.");
        return MapToResponse(link, baseUrl);
    }

    public async Task DeactivateAsync(Guid tenantId, Guid id)
    {
        var link = await linkRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("PaymentLink.NotFound", "Link de pagamento nao encontrado.");
        link.Deactivate();
        linkRepository.Update(link);
        await linkRepository.SaveChangesAsync();
    }

    public async Task<PaymentLinkResponseDto> UpdateAsync(Guid tenantId, Guid id, UpdatePaymentLinkDto request, string baseUrl)
    {
        var link = await linkRepository.GetByIdAsync(tenantId, id)
            ?? throw new NotFoundException("PaymentLink.NotFound", "Link de pagamento nao encontrado.");

        // Quando o caller pede uma rule nova, validamos ownership/atividade igual ao Create.
        if (request.SplitRuleId.HasValue && request.SplitRuleId.Value != Guid.Empty)
        {
            var rule = await splitRuleRepository.GetByIdAsync(tenantId, request.SplitRuleId.Value)
                ?? throw new NotFoundException("PaymentLink.SplitRuleNotFound", "Regra de split nao encontrada.");
            if (!rule.IsActive)
                throw new ValidationException("PaymentLink.SplitRuleInactive", "A regra de split selecionada esta inativa.");
        }

        link.UpdateMutable(
            request.Description, request.MaxUses, request.ExpiresAt, request.SplitRuleId, request.PaymentTypes,
            advanceOptIn: request.AdvanceOptIn,
            resetAdvanceOptInWhenNull: request.AdvanceOptInReset);
        linkRepository.Update(link);
        await linkRepository.SaveChangesAsync();

        return MapToResponse(link, baseUrl);
    }

    public async Task<PaymentLinkTransactionStatusDto> GetTransactionStatusAsync(string token, Guid transactionId)
    {
        var link = await linkRepository.GetByTokenAsync(token)
            ?? throw new NotFoundException("PaymentLink.NotFound", "Link de pagamento nao encontrado.");

        // Scope by tenant so the public token only exposes status of transactions in
        // the same tenant as the link. Cross-tenant probing returns 404 via NotFoundException
        // raised inside GetByIdAsync.
        var tx = await transactionService.GetByIdAsync(link.TenantId, transactionId);
        var status = tx.Status;
        var terminal = status is TransactionStatus.CAPTURED
            or TransactionStatus.DECLINED
            or TransactionStatus.FAILED
            or TransactionStatus.REFUNDED
            or TransactionStatus.VOIDED
            or TransactionStatus.CHARGEBACKERROR;

        return new PaymentLinkTransactionStatusDto(status.ToString(), terminal);
    }

    public async Task<PaymentLinkResolveDto> ResolveAsync(string token)
    {
        var link = await linkRepository.GetByTokenAsync(token)
            ?? throw new NotFoundException("PaymentLink.NotFound", "Link de pagamento nao encontrado.");

        if (!link.IsValid())
            throw new BusinessException("PaymentLink.Expired", "Link de pagamento expirado ou esgotado.");

        // Resolve a public-safe seller display name for the checkout. Prefer TradeName
        // (nome fantasia) when set, fallback to LegalName (razão social). NEVER expose
        // CNPJ, internal IDs, payout config, contact, etc — this endpoint is anonymous.
        string? sellerName = null;
        if (link.SellerId.HasValue)
        {
            var seller = await sellerRepository.GetByIdAsync(link.TenantId, link.SellerId.Value);
            if (seller is not null)
                sellerName = !string.IsNullOrWhiteSpace(seller.TradeName) ? seller.TradeName : seller.LegalName;
        }

        var allowed = link.GetEffectivePaymentTypes().Select(t => t.ToString()).ToArray();

        return new PaymentLinkResolveDto(
            link.Amount,
            link.PaymentType.ToString(),
            allowed,
            link.Installments,
            link.Description,
            sellerName);
    }

    public async Task<TransactionResponseDto> PayAsync(string token, PayPaymentLinkDto request)
    {
        var link = await linkRepository.GetByTokenAsync(token)
            ?? throw new NotFoundException("PaymentLink.NotFound", "Link de pagamento nao encontrado.");

        if (!link.IsValid())
            throw new BusinessException("PaymentLink.Expired", "Link de pagamento expirado ou esgotado.");

        // Resolve qual método o cliente escolheu. Em links multi-método o cliente
        // tem que mandar ChosenPaymentType e este precisa estar entre os permitidos.
        // Em links single-method legacy aceitamos ausência (= usa o PaymentType do link).
        PaymentType effectiveMethod;
        var allowed = link.GetEffectivePaymentTypes();
        if (allowed.Count == 1)
        {
            // Single-method (legacy ou multi-com-1): ignora ChosenPaymentType, usa o único.
            effectiveMethod = allowed[0];
        }
        else
        {
            if (!request.ChosenPaymentType.HasValue)
                throw new ValidationException("PaymentLink.MethodRequired",
                    "Este link aceita múltiplos métodos. Informe o método escolhido.");
            if (!link.AcceptsPaymentType(request.ChosenPaymentType.Value))
                throw new ValidationException("PaymentLink.MethodNotAllowed",
                    "Método de pagamento informado não é permitido por este link.");
            effectiveMethod = request.ChosenPaymentType.Value;
        }

        // Pix and Boleto rails require payer name + CPF/CNPJ + email up front because the
        // generated charge (QR code / boleto) carries that data on the printed instrument
        // itself. Card-typed methods don't need it here — Stripe Payment Element collects
        // billing details on the frontend and the wallet/card supplies the rest at confirm time.
        if (effectiveMethod is PaymentType.PIX or PaymentType.BOLETO)
        {
            if (string.IsNullOrWhiteSpace(request.PayerName)
                || string.IsNullOrWhiteSpace(request.PayerDocument)
                || string.IsNullOrWhiteSpace(request.PayerEmail))
                throw new ValidationException("PaymentLink.PayerRequired",
                    "Nome, CPF/CNPJ e email do pagador são obrigatórios para Pix e Boleto.");
        }

        var attempt = await linkRepository.TryReserveUsageAsync(link.Id);
        if (attempt == null)
            throw new BusinessException("PaymentLink.Expired", "Link de pagamento expirado ou esgotado.");

        // Resolve the split rule snapshot at pay time. If the rule was deactivated between
        // link creation and payment, surface that as a business error rather than silently
        // dropping the split — the link explicitly promised this allocation.
        Guid? splitRuleId = null;
        if (link.SplitRuleId.HasValue)
        {
            var rule = await splitRuleRepository.GetByIdAsync(link.TenantId, link.SplitRuleId.Value);
            if (rule == null || !rule.IsActive)
            {
                await linkRepository.FailUsageAttemptAsync(attempt.Id);
                throw new ValidationException("PaymentLink.SplitRuleInactive",
                    "A regra de split associada a este link nao esta mais ativa.");
            }
            splitRuleId = rule.Id;
        }

        // Card-typed links can be paid wallet-first without a payer form; the real billing
        // details flow through Stripe at confirm time. To keep the PayerDto validator strict
        // for the transactions API, substitute identifiable placeholders when fields are
        // missing on a card flow. The DB row carries the placeholder until a webhook updates
        // it (TODO: enrich Transaction.Payer from PaymentIntent.charges[0].billing_details).
        var isCard = effectiveMethod is PaymentType.CREDIT_CARD or PaymentType.DEBIT_CARD;
        string payerName, payerDocument, payerEmail;
        if (isCard)
        {
            payerName = !string.IsNullOrWhiteSpace(request.PayerName) ? request.PayerName : "Cliente Fellow Pay";
            payerDocument = !string.IsNullOrWhiteSpace(request.PayerDocument) ? request.PayerDocument : "00000000000";
            payerEmail = !string.IsNullOrWhiteSpace(request.PayerEmail) ? request.PayerEmail : "checkout@fellowpay.com.br";
        }
        else
        {
            // Guard at the top of this method already ensured non-null for Pix/Boleto.
            payerName = request.PayerName!;
            payerDocument = request.PayerDocument!;
            payerEmail = request.PayerEmail!;
        }

        // Parcelas efetivas: cliente escolheu no checkout (request.ChosenInstallments)
        // ou fallback pro link.Installments. Clamp pelo cap do seller (sem juros) pra
        // evitar bypass do cap via request manipulado. Só vale pra CREDIT_CARD.
        var effectiveInstallments = request.ChosenInstallments ?? link.Installments;
        if (effectiveMethod == PaymentType.CREDIT_CARD && link.SellerId.HasValue && effectiveInstallments > 1)
        {
            var cap = await pricingService.GetEffectiveMaxInstallmentsAsync(link.TenantId, link.SellerId.Value);
            if (effectiveInstallments > cap) effectiveInstallments = cap;
        }
        if (effectiveInstallments < 1) effectiveInstallments = 1;

        var createTx = new CreateTransactionDto(
            SellerId: link.SellerId,
            Amount: link.Amount,
            PaymentType: effectiveMethod,
            Installments: effectiveInstallments,
            Description: link.Description ?? "Pagamento via link",
            Payer: new PayerDto(payerName, payerDocument, payerEmail, request.PayerPhone),
            SplitRuleId: splitRuleId,
            AdvanceOptIn: link.AdvanceOptIn); // propaga override per-link → per-TX

        try
        {
            var result = await transactionService.CreateAsync(link.TenantId, createTx);

            // The provider call doesn't always throw — Stripe/OpenPix failures may surface
            // as a Transaction with Status = FAILED/DECLINED instead. We must roll back
            // the link's usage in that case so a transient provider error doesn't burn a
            // one-time link, and so the customer can retry.
            if (result.Status is TransactionStatus.FAILED or TransactionStatus.DECLINED)
            {
                await linkRepository.FailUsageAttemptAsync(attempt.Id);
                throw new ValidationException(
                    "PaymentLink.PaymentFailed",
                    "Não foi possível processar o pagamento. Tente novamente em instantes.");
            }

            await linkRepository.CompleteUsageAttemptAsync(attempt.Id, result.InternalId);
            return result;
        }
        catch
        {
            await linkRepository.FailUsageAttemptAsync(attempt.Id);
            throw;
        }
    }

    private PaymentLinkResponseDto MapToResponse(PaymentLink l, string baseUrl)
    {
        var publicBase = ResolvePublicBase(baseUrl);
        var allowed = l.GetEffectivePaymentTypes().ToArray();
        return new(l.Id, l.Token, $"{publicBase}/pay/{l.Token}",
            l.Amount, l.PaymentType, allowed, l.Installments, l.Description,
            l.MaxUses, l.UsageCount, l.Active, l.ExpiresAt, l.CreatedAt, l.SellerId, l.SplitRuleId,
            l.AdvanceOptIn);
    }

    public async Task<IReadOnlyList<PaymentLinkInstallmentOptionDto>> GetInstallmentOptionsAsync(string token)
    {
        var link = await linkRepository.GetByTokenAsync(token)
            ?? throw new NotFoundException("PaymentLink.NotFound", "Link de pagamento nao encontrado.");

        // Sem CREDIT_CARD aceito, não há o que parcelar.
        if (!link.GetEffectivePaymentTypes().Contains(PaymentType.CREDIT_CARD))
            return Array.Empty<PaymentLinkInstallmentOptionDto>();

        // Sem seller anexado (link da plataforma): default à vista, sem plan/cap.
        if (!link.SellerId.HasValue)
            return new[] { new PaymentLinkInstallmentOptionDto(1, link.Amount, link.Amount) };

        // Provider Stripe é o único que suporta installments no roteamento atual.
        var fullOptions = await pricingService.GetInstallmentOptionsAsync(
            link.TenantId, link.SellerId.Value, PaymentProvider.STRIPE, link.Amount);

        // O DTO público omite breakdown interno de fees — comprador só precisa
        // saber quantos × por quanto. Total sempre = amount (modo sem juros).
        return fullOptions
            .Select(o => new PaymentLinkInstallmentOptionDto(o.Count, o.PerInstallmentAmount, o.Total))
            .ToList();
    }
}
