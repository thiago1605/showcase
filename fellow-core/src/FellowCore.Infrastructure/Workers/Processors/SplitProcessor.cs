using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FellowCore.Infrastructure.Workers.Processors;

public class SplitProcessor(
    ITransactionRepository transactionRepository,
    ISplitTransferRepository splitTransferRepository,
    ISplitAllocationRepository splitAllocationRepository,
    ILedgerService ledgerService,
    ISplitCalculationService splitCalculationService,
    IItemSplitResolver itemSplitResolver,
    ILogger<SplitProcessor> logger) : ISplitProcessor
{
    public async Task ProcessAllPendingSplitsAsync(CancellationToken ct = default)
    {
        var batch = await transactionRepository.GetPendingSplitBatchAsync();
        if (batch.Count == 0) return;

        logger.LogInformation("SplitProcessor: {Count} transação(ões) com splits pendentes", batch.Count);

        foreach (var (tenantId, txId) in batch)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessSplitsForTransactionAsync(tenantId, txId, ct);
        }
    }

    public async Task ProcessSplitsForTransactionAsync(Guid transactionId, CancellationToken ct = default)
    {
        var transaction = await transactionRepository.GetByIdWithSplitsAsync(transactionId);
        if (transaction is null)
        {
            logger.LogWarning("Transação {TransactionId} não encontrada para split", transactionId);
            return;
        }

        await ProcessTransactionSplitsInternal(transaction, ct);
    }

    private async Task ProcessSplitsForTransactionAsync(Guid tenantId, Guid transactionId, CancellationToken ct)
    {
        var transaction = await transactionRepository.GetByIdWithSplitsAsync(tenantId, transactionId);
        if (transaction is null)
        {
            logger.LogWarning("Transação {TransactionId} não encontrada para split (tenant: {TenantId})", transactionId, tenantId);
            return;
        }

        await ProcessTransactionSplitsInternal(transaction, ct);
    }

    private async Task ProcessTransactionSplitsInternal(Transaction transaction, CancellationToken ct)
    {
        if (transaction.Status != TransactionStatus.CAPTURED)
        {
            logger.LogWarning("Transação {TransactionId} não está CAPTURED (status: {Status}), ignorando splits",
                transaction.Id, transaction.Status);
            return;
        }

        if (!transaction.SellerId.HasValue)
        {
            logger.LogWarning("Transação {TransactionId} não tem SellerId (primary seller), ignorando splits", transaction.Id);
            return;
        }

        var primarySellerId = transaction.SellerId.Value;

        // Also recover stale SCHEDULED splits (stuck > 10 minutes due to a prior crash)
        var pendingSplits = transaction.Splits
            .Where(s => s.Status == SplitStatus.PENDING ||
                        (s.Status == SplitStatus.SCHEDULED && s.CreatedAt < DateTime.UtcNow.AddMinutes(-10)))
            .ToList();

        if (pendingSplits.Count == 0) return;

        logger.LogInformation("Processando {Count} split(s) para transação {TransactionId}",
            pendingSplits.Count, transaction.Id);

        // Build calculation input from transaction splits
        var recipients = pendingSplits
            .Where(s => Guid.TryParse(s.RecipientId, out _))
            .Select(s => new SplitRecipientInput(
                SellerId: Guid.Parse(s.RecipientId),
                FixedAmount: s.Percentage.HasValue ? null : s.Amount,
                Percentage: s.Percentage,
                Priority: 0))
            .ToList();

        var feePolicy = transaction.FeeAllocationPolicy;

        // SPLIT_CLEARING already contains only the NetAmount (fee was extracted at platform level
        // into PLATFORM_FEE/PROVIDER_COST/PLATFORM_MARGIN accounts during capture).
        // The fee allocation policy is informational for reporting — actual distribution is of net only.

        var calcResult = splitCalculationService.Calculate(new SplitCalculationInput(
            GrossAmount: transaction.Amount,
            NetAmount: transaction.NetAmount ?? transaction.Amount,
            PlatformFee: 0m,
            ProviderCost: 0m,
            Recipients: recipients,
            FeePolicy: feePolicy
        ));

        foreach (var split in pendingSplits)
        {
            if (ct.IsCancellationRequested) break;

            if (!Guid.TryParse(split.RecipientId, out var sellerId))
            {
                logger.LogWarning("RecipientId inválido no split {SplitId}: {RecipientId}", split.Id, split.RecipientId);
                split.MarkAsFailed();
                continue;
            }

            // Find the calculated net share for this recipient
            var recipientCalc = calcResult.Recipients.FirstOrDefault(r => r.SellerId == sellerId);
            var creditAmount = recipientCalc?.NetShare ?? split.Amount;

            if (creditAmount <= 0)
            {
                logger.LogWarning("Split {SplitId} tem valor líquido <= 0 após fee allocation. Marcando como pago (zero).", split.Id);
                split.MarkAsPaid();
                continue;
            }

            try
            {
                // 1. Check idempotency — lookup existing marker for this recipient
                var existing = await splitTransferRepository.GetByTransactionAndRecipientAsync(
                    transaction.TenantId, transaction.Id, sellerId, isPrimaryShare: false);

                if (existing != null)
                {
                    // PAID = already distributed, skip
                    if (existing.Status == SplitTransferStatus.PAID)
                    {
                        split.MarkAsPaid();
                        continue;
                    }
                    // RESERVED/PROCESSING = marker exists but ledger may not have completed — retry the ledger
                    if (existing.Status is SplitTransferStatus.RESERVED or SplitTransferStatus.PROCESSING)
                    {
                        logger.LogWarning("SplitTransfer {TransferId} stuck in {Status}, retrying ledger for TX {TxId} / Seller {SellerId}",
                            existing.Id, existing.Status, transaction.Id, sellerId);

                        await ledgerService.DistributeFromClearingAsync(
                            transaction.TenantId, sellerId, creditAmount,
                            $"Split TX {transaction.Id} → Seller {sellerId}",
                            transaction.Id.ToString(),
                            idempotencyKey: existing.Id.ToString());

                        existing.MarkProcessing();
                        existing.MarkPaid();
                        splitTransferRepository.Update(existing);
                        await splitTransferRepository.SaveChangesAsync();
                        split.MarkAsPaid();
                        continue;
                    }
                    // FAILED = can retry with new marker (delete old or let unique index handle)
                    // Fall through to create new marker
                }

                // 2. Create SplitTransfer marker and persist BEFORE ledger
                var transferResult = SplitTransfer.Create(
                    transaction.Id, transaction.TenantId, sellerId, creditAmount, split.Percentage);
                if (transferResult.IsFailure)
                {
                    logger.LogError("Falha ao criar SplitTransfer: {Error}", transferResult.Error.Description);
                    split.MarkAsFailed();
                    continue;
                }
                var transfer = transferResult.Value;
                transfer.Reserve();
                splitTransferRepository.Add(transfer);
                await splitTransferRepository.SaveChangesAsync();

                // 3. Execute ledger distribution
                await ledgerService.DistributeFromClearingAsync(
                    transaction.TenantId, sellerId, creditAmount,
                    $"Split TX {transaction.Id} → Seller {sellerId}",
                    transaction.Id.ToString(),
                    idempotencyKey: transfer.Id.ToString());

                // 4. Mark PAID only after ledger succeeds
                transfer.MarkProcessing();
                transfer.MarkPaid();
                splitTransferRepository.Update(transfer);
                await splitTransferRepository.SaveChangesAsync();
                split.MarkAsPaid();

                logger.LogInformation(
                    "Split {SplitId}: R${Amount} distribuído para seller {RecipientSellerId} (SplitTransfer {TransferId})",
                    split.Id, creditAmount, sellerId, transfer.Id);
            }
            catch (Exception ex)
            {
                // Try to mark the existing SplitTransfer as FAILED to allow retry on next run
                try
                {
                    var failedMarker = await splitTransferRepository.GetByTransactionAndRecipientAsync(
                        transaction.TenantId, transaction.Id, sellerId, isPrimaryShare: false);
                    if (failedMarker != null && failedMarker.Status is SplitTransferStatus.RESERVED or SplitTransferStatus.PROCESSING)
                    {
                        failedMarker.Fail($"Ledger error: {ex.Message}");
                        splitTransferRepository.Update(failedMarker);
                        await splitTransferRepository.SaveChangesAsync();
                    }
                }
                catch (Exception innerEx)
                {
                    logger.LogError(innerEx, "Falha ao marcar SplitTransfer como FAILED para split {SplitId}", split.Id);
                }

                // Do NOT mark TransactionSplit as FAILED — keep it PENDING so batch picks it up again
                logger.LogError(ex, "Falha ao processar split {SplitId} para seller {SellerId}. Split mantido PENDING para retry.", split.Id, sellerId);
            }
        }

        // Distribute primary seller's remaining share from SPLIT_CLEARING → primary WALLET
        decimal totalDistributed = pendingSplits
            .Where(s => s.Status == SplitStatus.PAID)
            .Sum(s =>
            {
                if (!Guid.TryParse(s.RecipientId, out var sid)) return 0m;
                var calc = calcResult.Recipients.FirstOrDefault(r => r.SellerId == sid);
                return calc?.NetShare ?? s.Amount;
            });

        decimal netAmount = transaction.NetAmount ?? transaction.Amount;
        decimal primaryShare = netAmount - totalDistributed;

        if (primaryShare > 0)
        {
            try
            {
                var existingPrimary = await splitTransferRepository.GetByTransactionAndRecipientAsync(
                    transaction.TenantId, transaction.Id, primarySellerId, isPrimaryShare: true);

                if (existingPrimary != null && existingPrimary.Status == SplitTransferStatus.PAID)
                {
                    // Already distributed — skip
                }
                else if (existingPrimary != null && existingPrimary.Status is SplitTransferStatus.RESERVED or SplitTransferStatus.PROCESSING)
                {
                    // Retry ledger for stuck marker
                    await ledgerService.DistributeFromClearingAsync(
                        transaction.TenantId, primarySellerId, primaryShare,
                        $"Split TX {transaction.Id} → Primary Seller {primarySellerId}",
                        transaction.Id.ToString(),
                        idempotencyKey: existingPrimary.Id.ToString());

                    existingPrimary.MarkProcessing();
                    existingPrimary.MarkPaid();
                    splitTransferRepository.Update(existingPrimary);
                    await splitTransferRepository.SaveChangesAsync();
                }
                else
                {
                    // existingPrimary is null OR FAILED → create new marker
                    var primaryTransferResult = SplitTransfer.Create(
                        transaction.Id, transaction.TenantId, primarySellerId, primaryShare, isPrimaryShare: true);
                    if (primaryTransferResult.IsSuccess)
                    {
                        var primaryTransfer = primaryTransferResult.Value;
                        primaryTransfer.Reserve();
                        splitTransferRepository.Add(primaryTransfer);
                        await splitTransferRepository.SaveChangesAsync();

                        await ledgerService.DistributeFromClearingAsync(
                            transaction.TenantId, primarySellerId, primaryShare,
                            $"Split TX {transaction.Id} → Primary Seller {primarySellerId}",
                            transaction.Id.ToString(),
                            idempotencyKey: primaryTransfer.Id.ToString());

                        primaryTransfer.MarkProcessing();
                        primaryTransfer.MarkPaid();
                        splitTransferRepository.Update(primaryTransfer);
                        await splitTransferRepository.SaveChangesAsync();

                        logger.LogInformation(
                            "Primary seller {PrimarySellerId} recebeu R${Amount} do SPLIT_CLEARING (TX {TxId})",
                            primarySellerId, primaryShare, transaction.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falha ao distribuir share do primary seller {SellerId} (R${Amount}) do SPLIT_CLEARING. TX: {TxId}",
                    primarySellerId, primaryShare, transaction.Id);
            }
        }

        // Create SplitAllocation records linking items → recipients → transfers for audit trail
        try
        {
            var resolution = await itemSplitResolver.ResolveFromItemsAsync(transaction.TenantId, transaction.Id);
            if (resolution.HasItemSplits)
            {
                var allocations = await splitAllocationRepository.GetByTransactionIdAsync(transaction.TenantId, transaction.Id);
                foreach (var allocation in allocations.Where(a => a.SplitTransferId == null))
                {
                    var matchingTransfer = await splitTransferRepository.GetByTransactionAndRecipientAsync(
                        transaction.TenantId, transaction.Id, allocation.RecipientSellerId, isPrimaryShare: false);
                    if (matchingTransfer != null)
                    {
                        allocation.LinkToSplitTransfer(matchingTransfer.Id);
                    }
                }
                await splitAllocationRepository.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            // Non-critical: allocation linking failure doesn't affect financial distribution
            logger.LogWarning(ex, "Falha ao criar/vincular SplitAllocation para TX {TxId}. Distribuição financeira já concluída.", transaction.Id);
        }

        transactionRepository.Update(transaction);
        await transactionRepository.SaveChangesAsync();
        await splitTransferRepository.SaveChangesAsync();
    }
}
