using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Email.Templates;
using FellowCore.Application.Modules.Notifications.DTOs;
using FellowCore.Application.Modules.Notifications.Interfaces;
using FellowCore.Application.Modules.Receipts;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Events;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Notifications.Handlers;

public class TransactionStatusChangedHandler(
    INotificationsService notificationsService,
    IEmailService emailService,
    IRealtimeNotifier realtimeNotifier,
    ITenantRepository tenantRepository,
    ISellerRepository sellerRepository,
    ICustomerRepository customerRepository,
    ITransactionRepository transactionRepository,
    IReceiptService receiptService,
    IReceiptRepository receiptRepository,
    ISplitProcessor splitProcessor,
    IProductRepository productRepository,
    ILogger<TransactionStatusChangedHandler> logger)
    : IDomainEventHandler<TransactionStatusChangedEvent>
{
    public async Task HandleAsync(TransactionStatusChangedEvent domainEvent, CancellationToken cancellationToken = default)
    {
        if (!domainEvent.SellerId.HasValue)
            return;

        // Webhook notification (existing behavior)
        var data = new NotificationJobData(
            TenantId: domainEvent.TenantId,
            SellerId: domainEvent.SellerId.Value,
            TransactionId: domainEvent.TransactionId,
            Status: domainEvent.NewStatus,
            NetAmount: domainEvent.NetAmount ?? 0M,
            ProviderTxId: domainEvent.ProviderTxId ?? string.Empty,
            PaymentType: domainEvent.PaymentType);

        notificationsService.NotifyTransactionUpdate(data);

        // Realtime notification
        await realtimeNotifier.SendToTenantAsync(domainEvent.TenantId, "transaction.status_changed", new
        {
            transactionId = domainEvent.TransactionId,
            status = domainEvent.NewStatus.ToString(),
            amount = domainEvent.NetAmount
        });

        // Email notification on COMPLETED
        if (domainEvent.NewStatus == TransactionStatus.CAPTURED)
        {
            await SendCompletedEmailAsync(domainEvent, cancellationToken);
            await SendCustomerReceiptEmailAsync(domainEvent, cancellationToken);
            // Marketplace: se a TX é de um produto (ExternalReferenceId = "product:{id}"),
            // dispara email de entrega pro comprador com link de acesso. Falha aqui
            // não rompe o fluxo de captura — split + recibo já rodaram.
            await SendProductDeliveryEmailAsync(domainEvent, cancellationToken);

            // Auto-process splits (creates payouts for each split recipient)
            try
            {
                await splitProcessor.ProcessSplitsForTransactionAsync(domainEvent.TransactionId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao processar splits para transação {TxId}", domainEvent.TransactionId);
            }
        }
    }

    /// <summary>
    /// Envia o email de "entrega" do produto pro comprador — diferente do recibo
    /// fiscal: este é focado em fazer o usuário acessar o produto digital
    /// imediatamente, via `Product.DeliveryUrl`.
    ///
    /// Acionado APENAS quando a TX é de um produto do marketplace, identificado
    /// pelo padrão `ExternalReferenceId = "product:{guid}"` (formato definido em
    /// MarketplaceCheckoutService). Outros tipos de TX (recurring, PaymentLink,
    /// etc) não disparam este email — o recibo customer já cobre.
    ///
    /// Idempotência: não há flag dedicado (como Receipt.IsCustomerEmailSent).
    /// Aceitável pq o handler roda via outbox dispatcher que já tem dedup por
    /// event id. Em prod, se houver caso real de duplicação, adicionar coluna
    /// `IsDeliveryEmailSent` em Receipt ou criar entidade dedicada.
    /// </summary>
    private async Task SendProductDeliveryEmailAsync(TransactionStatusChangedEvent e, CancellationToken ct)
    {
        try
        {
            var tx = await transactionRepository.GetByIdAsync(e.TenantId, e.TransactionId);
            if (tx is null) return;

            // Filtro 1: só TXs de marketplace (formato "product:{guid}")
            const string prefix = "product:";
            if (string.IsNullOrEmpty(tx.ExternalReferenceId) ||
                !tx.ExternalReferenceId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return;
            }

            if (!Guid.TryParse(tx.ExternalReferenceId.AsSpan(prefix.Length), out var productId))
            {
                logger.LogWarning(
                    "[DELIVERY_EMAIL] ExternalReferenceId '{Ref}' começa com 'product:' mas não tem GUID válido — TX {TxId}",
                    tx.ExternalReferenceId, tx.Id);
                return;
            }

            var product = await productRepository.GetByIdAsync(e.TenantId, productId);
            if (product is null)
            {
                logger.LogWarning(
                    "[DELIVERY_EMAIL] Product {ProductId} não encontrado pra TX {TxId} — produto deletado depois da venda?",
                    productId, tx.Id);
                return;
            }

            // Resolve buyer email — prefere PayerEmail (capturado no checkout
            // público), fallback pro Customer se vinculado. Sem email = não envia.
            var buyerEmail = tx.PayerEmail;
            var buyerName = tx.PayerName;
            if (string.IsNullOrWhiteSpace(buyerEmail) && tx.CustomerId.HasValue)
            {
                var customer = await customerRepository.GetByIdAsync(e.TenantId, tx.CustomerId.Value);
                if (customer != null)
                {
                    buyerEmail = customer.Email;
                    buyerName ??= customer.Name;
                }
            }

            if (string.IsNullOrWhiteSpace(buyerEmail))
            {
                logger.LogInformation(
                    "[DELIVERY_EMAIL] Sem email do buyer pra TX {TxId} produto {ProductId} — pulando entrega",
                    tx.Id, productId);
                return;
            }

            // Nome do produtor pro email — TradeName preferido, fallback LegalName,
            // último fallback nome do tenant. Buyer geralmente conhece o produtor
            // pelo brand (TradeName).
            var producerName = "o produtor";
            if (tx.SellerId.HasValue)
            {
                var producer = await sellerRepository.GetByIdAsync(e.TenantId, tx.SellerId.Value);
                producerName = producer?.TradeName ?? producer?.LegalName ?? producerName;
            }

            var tenant = await tenantRepository.GetByIdWithConfigAsync(e.TenantId);
            var tenantName = tenant?.Name ?? "Fellow Pay";

            var htmlBody = EmailTemplates.ProductDeliveryToBuyer(
                tenantName: tenantName,
                buyerName: buyerName ?? "Cliente",
                productName: product.Name,
                deliveryUrl: product.DeliveryUrl,
                producerName: producerName,
                amount: tx.Amount);

            // Assunto muda dependendo se tem link de entrega:
            //   COM link → "Seu acesso ao X está liberado" (ação imediata)
            //   SEM link → "Sua compra de X foi confirmada" (aguarde contato)
            var subject = string.IsNullOrEmpty(product.DeliveryUrl)
                ? $"Sua compra de {product.Name} foi confirmada"
                : $"Seu acesso a {product.Name} está liberado";

            var message = new EmailMessage(
                To: buyerEmail,
                ToName: buyerName ?? "Cliente",
                Subject: subject,
                HtmlBody: htmlBody);

            bool sent = await emailService.SendAsync(message, ct);
            if (sent)
            {
                logger.LogInformation(
                    "[DELIVERY_EMAIL] Entrega enviada pra {EmailMask} — TX {TxId} produto {ProductId}",
                    buyerEmail[..Math.Min(3, buyerEmail.Length)] + "***", tx.Id, productId);
            }
            else
            {
                // Provider não confirmou — log warning. ResendEmailProvider tem
                // retry interno; se chegou aqui sem confirmação, provavelmente é
                // ApiKey ausente em dev ou 4xx persistente. Não fazemos retry
                // adicional aqui pq o outbox dispatcher pode reprocessar o evento.
                logger.LogWarning(
                    "[DELIVERY_EMAIL] Provider não confirmou envio pra TX {TxId} produto {ProductId}",
                    tx.Id, productId);
            }
        }
        catch (Exception ex)
        {
            // Best-effort: erro aqui NÃO deve quebrar o fluxo de captura
            // (split + recibo já rodaram nas linhas anteriores). Log e segue.
            logger.LogError(ex,
                "[DELIVERY_EMAIL] Falha ao enviar email de entrega pra TX {TxId}",
                e.TransactionId);
        }
    }

    private async Task SendCompletedEmailAsync(TransactionStatusChangedEvent e, CancellationToken ct)
    {
        try
        {
            var tenant = await tenantRepository.GetByIdWithConfigAsync(e.TenantId);
            if (tenant is null) return;

            var seller = await sellerRepository.GetByIdAsync(e.TenantId, e.SellerId!.Value);
            if (seller is null || string.IsNullOrWhiteSpace(seller.Email)) return;

            var message = new EmailMessage(
                To: seller.Email,
                ToName: seller.LegalName,
                Subject: $"Pagamento confirmado — {e.NetAmount:C2}",
                HtmlBody: EmailTemplates.TransactionCompleted(
                    tenant.Name,
                    e.TransactionId.ToString(),
                    e.NetAmount ?? 0m,
                    e.PaymentType.ToString(),
                    e.OccurredAt)
            );

            await emailService.SendAsync(message, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao enviar email de transação concluída {TxId}", e.TransactionId);
        }
    }

    private async Task SendCustomerReceiptEmailAsync(TransactionStatusChangedEvent e, CancellationToken ct)
    {
        try
        {
            // Load transaction to get payer info
            var tx = await transactionRepository.GetByIdAsync(e.TenantId, e.TransactionId);
            if (tx is null) return;

            // Resolve payer email: prefer PayerEmail, fallback to Customer entity
            var payerEmail = tx.PayerEmail;
            var payerName = tx.PayerName;

            if (string.IsNullOrWhiteSpace(payerEmail) && tx.CustomerId.HasValue)
            {
                var customer = await customerRepository.GetByIdAsync(e.TenantId, tx.CustomerId.Value);
                if (customer != null)
                {
                    payerEmail = customer.Email;
                    payerName ??= customer.Name;
                }
            }

            if (string.IsNullOrWhiteSpace(payerEmail))
            {
                logger.LogDebug("Sem e-mail do pagador para TX {TxId}, ignorando recibo ao cliente", e.TransactionId);
                return;
            }

            // Generate or retrieve receipt (idempotent)
            var receipt = await receiptService.GenerateForPaymentAsync(e.TenantId, e.TransactionId);

            // Idempotency: skip if email already sent for this receipt
            if (receipt.IsCustomerEmailSent)
            {
                logger.LogDebug("Recibo {ReceiptId} já enviado para TX {TxId}", receipt.Id, e.TransactionId);
                return;
            }

            var tenant = await tenantRepository.GetByIdWithConfigAsync(e.TenantId);
            var tenantName = tenant?.Name ?? "Fellow Pay";

            var htmlBody = EmailTemplates.PaymentReceiptCustomer(
                tenantName,
                payerName ?? "Cliente",
                e.TransactionId.ToString(),
                tx.Amount,
                tx.PaymentType.ToString(),
                tx.CreatedAt,
                receipt.PublicUrl);

            var message = new EmailMessage(
                To: payerEmail,
                ToName: payerName ?? "Cliente",
                Subject: $"Comprovante de pagamento — R$ {tx.Amount:N2}",
                HtmlBody: htmlBody);

            // Só marca como enviado quando o provider confirma — sem isso, ApiKey
            // ausente / 4xx silenciosos faziam o flag virar true sem entrega real.
            bool sent = await emailService.SendAsync(message, ct);
            if (!sent)
            {
                receipt.RecordCustomerEmailFailure("Provedor de email não confirmou entrega.");
                await receiptRepository.SaveChangesAsync();
                logger.LogWarning("[RECEIPT] Email NOT sent for TX {TxId} (provedor não confirmou)", e.TransactionId);
                return;
            }

            receipt.MarkCustomerEmailSent();
            await receiptRepository.SaveChangesAsync();

            logger.LogInformation("[RECEIPT] Customer email sent for TX {TxId} to {Email}", e.TransactionId, payerEmail[..Math.Min(3, payerEmail.Length)] + "***");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao enviar email de comprovante ao cliente TX {TxId}", e.TransactionId);

            // Record failure on receipt for observability (best-effort)
            try
            {
                var receipt = await receiptRepository.GetByTransactionIdAsync(e.TenantId, e.TransactionId, ReceiptType.PAYMENT);
                if (receipt != null)
                {
                    receipt.RecordCustomerEmailFailure(ex.Message);
                    await receiptRepository.SaveChangesAsync();
                }
            }
            catch (Exception inner)
            {
                logger.LogWarning(inner, "Falha ao registrar erro de email no recibo TX {TxId}", e.TransactionId);
            }
        }
    }
}
