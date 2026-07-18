using FellowCore.Application.Exceptions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Receipts;

public class ReceiptService(
    IReceiptRepository receiptRepository,
    ITransactionRepository transactionRepository,
    IPayoutRepository payoutRepository,
    IRefundIntentRepository refundIntentRepository,
    ILogger<ReceiptService> logger) : IReceiptService
{
    public async Task<Receipt> GenerateForPaymentAsync(Guid tenantId, Guid transactionId)
    {
        // Idempotency: check if receipt already exists
        var existing = await receiptRepository.GetByTransactionIdAsync(tenantId, transactionId, ReceiptType.PAYMENT);
        if (existing != null) return existing;

        var tx = await transactionRepository.GetByIdAsync(tenantId, transactionId)
            ?? throw new NotFoundException("Transaction", transactionId.ToString());

        if (tx.Status != TransactionStatus.CAPTURED)
            throw new BusinessException("Receipt.InvalidStatus", "Recibo só pode ser gerado para transações capturadas.");

        var sellerId = tx.SellerId
            ?? throw new BusinessException("Receipt.NoSeller", "Transação sem seller associado.");

        var receipt = Receipt.Create(
            tenantId: tenantId,
            sellerId: sellerId,
            type: ReceiptType.PAYMENT,
            provider: tx.Provider,
            amount: tx.Amount,
            transactionId: transactionId,
            providerReceiptId: tx.ProviderTxId,
            description: $"Pagamento {tx.PaymentType} - R${tx.Amount:F2}");

        receiptRepository.Add(receipt);
        await receiptRepository.SaveChangesAsync();

        logger.LogInformation("[RECEIPT] Payment receipt {ReceiptId} generated for TX {TxId}", receipt.Id, transactionId);
        return receipt;
    }

    public async Task<Receipt> GenerateForRefundAsync(Guid tenantId, Guid refundIntentId)
    {
        var existing = await receiptRepository.GetByRefundIntentIdAsync(tenantId, refundIntentId);
        if (existing != null) return existing;

        var refund = await refundIntentRepository.GetByIdAsync(refundIntentId)
            ?? throw new NotFoundException("RefundIntent", refundIntentId.ToString());

        if (refund.Status != RefundIntentStatus.COMPLETED)
            throw new BusinessException("Receipt.InvalidStatus", "Recibo de reembolso requer refund completado.");

        var tx = await transactionRepository.GetByIdAsync(tenantId, refund.TransactionId)
            ?? throw new NotFoundException("Transaction", refund.TransactionId.ToString());

        var sellerId = tx.SellerId
            ?? throw new BusinessException("Receipt.NoSeller", "Transação sem seller associado.");

        var receipt = Receipt.Create(
            tenantId: tenantId,
            sellerId: sellerId,
            type: ReceiptType.REFUND,
            provider: tx.Provider,
            amount: refund.Amount,
            transactionId: refund.TransactionId,
            refundIntentId: refundIntentId,
            providerReceiptId: refund.ProviderRefundId,
            description: $"Reembolso - R${refund.Amount:F2}");

        receiptRepository.Add(receipt);
        await receiptRepository.SaveChangesAsync();

        logger.LogInformation("[RECEIPT] Refund receipt {ReceiptId} generated for RefundIntent {RefundId}", receipt.Id, refundIntentId);
        return receipt;
    }

    public async Task<Receipt> GenerateForPayoutAsync(Guid tenantId, Guid payoutId)
    {
        bool exists = await receiptRepository.ExistsAsync(tenantId, null, payoutId, null, ReceiptType.PAYOUT);
        if (exists)
        {
            var found = await receiptRepository.GetByPayoutIdAsync(tenantId, payoutId);
            if (found != null) return found;
        }

        var payout = await payoutRepository.GetByIdAsync(tenantId, payoutId)
            ?? throw new NotFoundException("Payout", payoutId.ToString());

        if (payout.Status != PayoutStatus.PAID)
            throw new BusinessException("Receipt.InvalidStatus", "Comprovante de saque requer payout PAID.");

        var receipt = Receipt.Create(
            tenantId: tenantId,
            sellerId: payout.SellerId,
            type: ReceiptType.PAYOUT,
            provider: payout.BankProvider,
            amount: payout.Amount - payout.Fee,
            payoutId: payoutId,
            providerReceiptId: payout.BankTransactionId,
            description: $"Saque - R${payout.Amount - payout.Fee:F2} (taxa: R${payout.Fee:F2})");

        receiptRepository.Add(receipt);
        await receiptRepository.SaveChangesAsync();

        logger.LogInformation("[RECEIPT] Payout receipt {ReceiptId} generated for Payout {PayoutId}", receipt.Id, payoutId);
        return receipt;
    }

    public async Task<Receipt> GenerateForSplitReceivedAsync(Guid tenantId, Guid transactionId, Guid recipientSellerId, decimal amount)
    {
        var existing = await receiptRepository.GetBySplitReceivedAsync(tenantId, transactionId, recipientSellerId);
        if (existing != null) return existing;

        var tx = await transactionRepository.GetByIdAsync(tenantId, transactionId)
            ?? throw new NotFoundException("Transaction", transactionId.ToString());

        var receipt = Receipt.Create(
            tenantId: tenantId,
            sellerId: recipientSellerId,
            type: ReceiptType.SPLIT_RECEIVED,
            provider: tx.Provider,
            amount: amount,
            transactionId: transactionId,
            description: $"Split recebido - R${amount:F2}");

        receiptRepository.Add(receipt);
        await receiptRepository.SaveChangesAsync();

        logger.LogInformation("[RECEIPT] Split receipt {ReceiptId} for seller {SellerId} TX {TxId}", receipt.Id, recipientSellerId, transactionId);
        return receipt;
    }

    public async Task<Receipt?> GetByIdAsync(Guid tenantId, Guid receiptId)
    {
        return await receiptRepository.GetByIdAsync(tenantId, receiptId);
    }

    public async Task<List<Receipt>> GetBySellerAsync(Guid tenantId, Guid sellerId, int limit = 50, int offset = 0)
    {
        return await receiptRepository.GetBySellerAsync(tenantId, sellerId, limit, offset);
    }
}
