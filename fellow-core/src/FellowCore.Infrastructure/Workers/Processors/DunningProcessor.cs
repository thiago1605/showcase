using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public class DunningProcessor(
    ITransactionRepository transactionRepository,
    IPaymentProviderFactory providerFactory,
    ILogger<DunningProcessor> logger) : IDunningProcessor
{
    public async Task ProcessDunningAsync(CancellationToken ct = default)
    {
        var transactions = await transactionRepository.GetDunningEligibleAsync(DateTime.UtcNow);

        if (transactions.Count == 0) return;

        logger.LogInformation("Processando dunning para {Count} transação(ões)", transactions.Count);

        foreach (var tx in transactions)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var provider = providerFactory.GetProvider(tx.Provider);

                // Tenta recriar o pagamento no provider
                var payer = new PayerDto("Retry", "00000000000", "retry@dunning.internal");
                var request = new CreateTransactionDto(
                    SellerId: tx.SellerId,
                    Amount: tx.Amount,
                    PaymentType: tx.PaymentType,
                    Installments: tx.Installments,
                    Description: $"Dunning retry #{tx.DunningAttempts + 1} para {tx.Id}",
                    Payer: payer);

                var result = await provider.ProcessPaymentAsync(
                    tx.Tenant, tx.Seller, request, tx.FeeAmount ?? 0);

                // Se o provider aceitou, marca sucesso e muda status para PROCESSING
                tx.RecordDunningAttempt(true);
                tx.UpdateStatus(Domain.Enums.TransactionStatus.PROCESSING);

                logger.LogInformation("Dunning bem-sucedido para transação {Id}, tentativa {Attempt}",
                    tx.Id, tx.DunningAttempts);
            }
            catch (Exception ex)
            {
                tx.RecordDunningAttempt(false);
                logger.LogWarning(ex, "Dunning falhou para transação {Id}, tentativa {Attempt}/{Max}",
                    tx.Id, tx.DunningAttempts, Domain.Entities.Transaction.MaxDunningAttempts);
            }

            transactionRepository.Update(tx);
        }

        await transactionRepository.SaveChangesAsync();
    }
}
