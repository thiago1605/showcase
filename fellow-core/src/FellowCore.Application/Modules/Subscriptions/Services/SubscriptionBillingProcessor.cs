using FellowCore.Application.Modules.Subscriptions.Interfaces;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Subscriptions.Services;

public class SubscriptionBillingProcessor(
    ISubscriptionRepository subscriptionRepository,
    ISellerRepository sellerRepository,
    ITransactionService transactionService,
    ILogger<SubscriptionBillingProcessor> logger) : ISubscriptionBillingProcessor
{
    public async Task ProcessDueBillingAsync(CancellationToken cancellationToken = default)
    {
        var dueSubscriptions = await subscriptionRepository.GetDueForBillingAsync(DateTime.UtcNow);

        if (dueSubscriptions.Count == 0)
        {
            logger.LogDebug("Nenhuma subscription devida para cobrança.");
            return;
        }

        logger.LogInformation("Processando {Count} subscriptions devidas para cobrança.", dueSubscriptions.Count);

        foreach (var subscription in dueSubscriptions)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                var seller = await sellerRepository.GetByIdAsync(subscription.TenantId, subscription.SellerId);
                if (seller == null || seller.Status != SellerStatus.ACTIVE)
                {
                    logger.LogWarning(
                        "Subscription {SubscriptionId} ignorada: seller {SellerId} nao esta ativo (status: {Status})",
                        subscription.Id, subscription.SellerId, seller?.Status);
                    continue;
                }

                var payer = new PayerDto(
                    Name: subscription.Customer?.Name ?? "Assinante",
                    Document: subscription.Customer?.Document ?? "00000000000",
                    Email: subscription.Customer?.Email ?? "assinante@fellowpay.com"
                );

                var transactionRequest = new CreateTransactionDto(
                    SellerId: subscription.SellerId,
                    Amount: subscription.Amount,
                    PaymentType: PaymentType.PIX,
                    Installments: 1,
                    Description: $"Assinatura #{subscription.Id:N} - Ciclo {subscription.CycleCount + 1}",
                    Payer: payer,
                    IdempotencyKey: $"sub-{subscription.Id:N}-cycle-{subscription.CycleCount + 1}"
                );

                var result = await transactionService.CreateAsync(subscription.TenantId, transactionRequest);

                subscription.AdvanceCycle();
                subscriptionRepository.Update(subscription);
                await subscriptionRepository.SaveChangesAsync();

                logger.LogInformation(
                    "Subscription {SubscriptionId} cobrada com sucesso. Ciclo: {Cycle} | TransactionId: {TransactionId} | Próxima cobrança: {NextBilling}",
                    subscription.Id, subscription.CycleCount, result.InternalId, subscription.NextBillingDate);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Erro ao processar cobrança da subscription {SubscriptionId}. Ciclo: {Cycle}",
                    subscription.Id, subscription.CycleCount + 1);
            }
        }
    }
}
