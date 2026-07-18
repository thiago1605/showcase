using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Ledgers.DTOs;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FellowCore.Application.Modules.Ledgers.Services;

public class LedgerService(ILedgerRepository ledgerRepository, ILogger<LedgerService> logger) : ILedgerService
{
    private const int MaxRetries = 5;

    public async Task<LedgerEntry> AddCreditAsync(Guid tenantId, Guid sellerId, CreateLedgerCreditDto dto)
    {
        LedgerAccountType targetAccountType = dto.BalanceType switch
        {
            "WAITING" => LedgerAccountType.FUTURE_RECEIVABLES,
            "AVAILABLE" => LedgerAccountType.WALLET,
            _ => dto.AccountType
        };

        return await ExecuteWithRetryAsync(async () =>
        {
            // Double-entry: debit platform receivable, credit seller account
            LedgerAccount platformAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_RECEIVABLE);
            LedgerAccount sellerAccount = await GetOrCreateAccountAsync(tenantId, sellerId, targetAccountType);

            // M4: Always create the platform credit entry unconditionally (3-entry pattern).
            // The old conditional auto-fund (only when balance < amount) caused ledger drift on
            // concurrency retries: if a first attempt persisted the auto-fund before the xmin conflict
            // was raised, a retry would skip re-adding it, leaving the books unbalanced.
            // Fix mirrors RecordIncomingFundsAsync: credit PLATFORM_RECEIVABLE first (external funds in),
            // then debit PLATFORM_RECEIVABLE + credit seller account (internal transfer).

            // 1) Credit PLATFORM_RECEIVABLE (external money in)
            var platformCredit = platformAccount.Credit(dto.Amount, $"Recebimento TX {dto.TransactionId}", "INCOMING_TRANSACTION", dto.TransactionId.ToString());
            if (platformCredit.IsFailure)
                throw new BusinessException(platformCredit.Error.Code, platformCredit.Error.Description);

            // 2) Debit PLATFORM_RECEIVABLE (internal transfer out to seller)
            var platformDebit = platformAccount.Debit(dto.Amount, $"Repasse seller — TX {dto.TransactionId}", "TRANSACTION", dto.TransactionId.ToString());
            if (platformDebit.IsFailure)
                throw new BusinessException(platformDebit.Error.Code, platformDebit.Error.Description);

            // 3) Credit seller account
            var sellerCredit = sellerAccount.Credit(dto.Amount, dto.Description, "TRANSACTION", dto.TransactionId.ToString());
            if (sellerCredit.IsFailure)
                throw new BusinessException(sellerCredit.Error.Code, sellerCredit.Error.Description);

            // Link contra entries (debit ↔ credit)
            platformDebit.Value.LinkContraEntry(sellerCredit.Value.Id);
            sellerCredit.Value.LinkContraEntry(platformDebit.Value.Id);

            ledgerRepository.AddEntry(platformCredit.Value);
            ledgerRepository.AddEntry(platformDebit.Value);
            ledgerRepository.AddEntry(sellerCredit.Value);

            await ledgerRepository.SaveChangesAsync();

            return sellerCredit.Value;
        }, "AddCredit");
    }

    public async Task<LedgerAccount> DebitSellerAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            // Try WALLET first; if insufficient, fall back to FUTURE_RECEIVABLES
            // (refunds/debits can occur before settlement moves funds to WALLET)
            LedgerAccount source = await FindFundedAccountAsync(tenantId, sellerId, amount);

            LedgerAccount platformPayout = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_PAYOUT);

            // Double-entry: debit seller account, credit platform payout
            var debitResult = source.Debit(amount, description, "PAYOUT", transactionId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = platformPayout.Credit(amount, $"Payout seller — {transactionId}", "PAYOUT", transactionId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();

            return source;
        }, "DebitSeller");
    }

    public async Task<LedgerBalanceResponse> GetBalanceAsync(Guid tenantId, Guid sellerId)
    {
        List<LedgerAccount> accounts = await ledgerRepository.GetAccountsBySellerAsync(tenantId, sellerId);
        decimal availableBalance = accounts.FirstOrDefault(a => a.Type == LedgerAccountType.WALLET)?.Balance ?? 0M;
        decimal waitingFunds = accounts.FirstOrDefault(a => a.Type == LedgerAccountType.FUTURE_RECEIVABLES)?.Balance ?? 0M;
        decimal disputed = accounts.FirstOrDefault(a => a.Type == LedgerAccountType.DISPUTE)?.Balance ?? 0M;

        return new LedgerBalanceResponse(Available: availableBalance, WaitingFunds: waitingFunds, Disputed: disputed, Total: availableBalance + waitingFunds + disputed);
    }

    public async Task ReversalCreditAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount wallet = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.WALLET, sellerId)
                ?? throw new NotFoundException("LedgerAccount.WalletNotFound", "Conta WALLET nao encontrada para este seller.");

            LedgerAccount platformPayout = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_PAYOUT);

            // Double-entry reversal: credit seller wallet, debit platform payout
            var creditResult = wallet.Credit(amount, description, "REVERSAL", transactionId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            var debitResult = platformPayout.Debit(amount, $"Estorno payout — {transactionId}", "REVERSAL", transactionId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            creditResult.Value.LinkContraEntry(debitResult.Value.Id);
            debitResult.Value.LinkContraEntry(creditResult.Value.Id);

            ledgerRepository.AddEntry(creditResult.Value);
            ledgerRepository.AddEntry(debitResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "ReversalCredit");
    }

    public async Task TransferFundsAsync(Guid tenantId, Guid sellerId, decimal amount)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount? futureReceivables = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.FUTURE_RECEIVABLES, sellerId);

            if (futureReceivables == null || futureReceivables.Balance <= 0) return 0;

            decimal amountToTransfer = Math.Min(amount, futureReceivables.Balance);

            LedgerAccount wallet = await GetOrCreateAccountAsync(tenantId, sellerId, LedgerAccountType.WALLET);

            var transferResult = futureReceivables.TransferTo(wallet, amountToTransfer);

            if (transferResult.IsFailure)
                throw new BusinessException(transferResult.Error.Code, transferResult.Error.Description);

            // TransferTo creates entries in both accounts — link them as contra-entries
            var debitEntry = futureReceivables.Entries.Last();
            var creditEntry = wallet.Entries.Last();
            debitEntry.LinkContraEntry(creditEntry.Id);
            creditEntry.LinkContraEntry(debitEntry.Id);

            ledgerRepository.AddEntry(debitEntry);
            ledgerRepository.AddEntry(creditEntry);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "TransferFunds");
    }

    public async Task RecordIncomingFundsAsync(Guid tenantId, Guid sellerId, decimal amount, LedgerAccountType accountType, string description)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            string refId = $"AUTO_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            LedgerAccount platformAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_RECEIVABLE);
            LedgerAccount sellerAccount = await GetOrCreateAccountAsync(tenantId, sellerId, accountType);

            // 1) Credit platform receivable (external money in)
            var platformCredit = platformAccount.Credit(amount, $"Recebimento — {description}", "INCOMING_TRANSACTION", refId);
            if (platformCredit.IsFailure)
                throw new BusinessException(platformCredit.Error.Code, platformCredit.Error.Description);

            // 2) Double-entry: debit platform receivable, credit seller account (internal transfer)
            var platformDebit = platformAccount.Debit(amount, $"Repasse seller — {description}", "INCOMING_TRANSACTION", refId);
            if (platformDebit.IsFailure)
                throw new BusinessException(platformDebit.Error.Code, platformDebit.Error.Description);

            var sellerCredit = sellerAccount.Credit(amount, description, "INCOMING_TRANSACTION", refId);
            if (sellerCredit.IsFailure)
                throw new BusinessException(sellerCredit.Error.Code, sellerCredit.Error.Description);

            platformDebit.Value.LinkContraEntry(sellerCredit.Value.Id);
            sellerCredit.Value.LinkContraEntry(platformDebit.Value.Id);

            ledgerRepository.AddEntry(platformCredit.Value);
            ledgerRepository.AddEntry(platformDebit.Value);
            ledgerRepository.AddEntry(sellerCredit.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "RecordIncomingFunds");
    }

    public async Task RecordDirectChargeFundsAsync(Guid tenantId, Guid sellerId, decimal sellerNetAmount, decimal feeAmount, LedgerAccountType accountType, string description)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            string refId = $"DIRECT_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            decimal grossAmount = sellerNetAmount + feeAmount;

            // Direct Charge: money goes directly to seller's connected Stripe account.
            // EXTERNAL_FUNDS represents funds in the connected account before internal allocation.
            LedgerAccount externalFunds = await GetOrCreateAccountAsync(tenantId, sellerId, LedgerAccountType.EXTERNAL_FUNDS);
            LedgerAccount sellerAccount = await GetOrCreateAccountAsync(tenantId, sellerId, accountType);

            // 1) Credit EXTERNAL_FUNDS (acknowledge gross amount arrived to connected account)
            var externalCredit = externalFunds.Credit(grossAmount, $"[Direct Charge] Recebimento — {description}", "INCOMING_DIRECT", refId);
            if (externalCredit.IsFailure)
                throw new BusinessException(externalCredit.Error.Code, externalCredit.Error.Description);
            ledgerRepository.AddEntry(externalCredit.Value);

            // 2) Double-entry: debit EXTERNAL_FUNDS, credit seller account (seller's net share)
            var externalDebitSeller = externalFunds.Debit(sellerNetAmount, $"[Direct Charge] Repasse seller — {description}", "INCOMING_DIRECT", refId);
            if (externalDebitSeller.IsFailure)
                throw new BusinessException(externalDebitSeller.Error.Code, externalDebitSeller.Error.Description);

            var sellerCredit = sellerAccount.Credit(sellerNetAmount, $"[Direct Charge] {description}", "INCOMING_DIRECT", refId);
            if (sellerCredit.IsFailure)
                throw new BusinessException(sellerCredit.Error.Code, sellerCredit.Error.Description);

            externalDebitSeller.Value.LinkContraEntry(sellerCredit.Value.Id);
            sellerCredit.Value.LinkContraEntry(externalDebitSeller.Value.Id);

            ledgerRepository.AddEntry(externalDebitSeller.Value);
            ledgerRepository.AddEntry(sellerCredit.Value);

            // 3) Double-entry: debit EXTERNAL_FUNDS, credit PLATFORM_FEE (application fee)
            if (feeAmount > 0)
            {
                LedgerAccount platformFee = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_FEE);

                var externalDebitFee = externalFunds.Debit(feeAmount, $"[Direct Charge] Application fee — {description}", "APPLICATION_FEE", refId);
                if (externalDebitFee.IsFailure)
                    throw new BusinessException(externalDebitFee.Error.Code, externalDebitFee.Error.Description);

                var feeCredit = platformFee.Credit(feeAmount, $"[Direct Charge] Application fee — {description}", "APPLICATION_FEE", refId);
                if (feeCredit.IsFailure)
                    throw new BusinessException(feeCredit.Error.Code, feeCredit.Error.Description);

                externalDebitFee.Value.LinkContraEntry(feeCredit.Value.Id);
                feeCredit.Value.LinkContraEntry(externalDebitFee.Value.Id);

                ledgerRepository.AddEntry(externalDebitFee.Value);
                ledgerRepository.AddEntry(feeCredit.Value);
            }

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "RecordDirectChargeFunds");
    }

    public async Task HoldDisputeAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            // Double-entry: debit seller funds, credit DISPUTE (funds frozen)
            // Try WALLET first; if insufficient, fall back to FUTURE_RECEIVABLES
            // (disputes can occur before settlement moves funds to WALLET)
            LedgerAccount source = await FindFundedAccountAsync(tenantId, sellerId, amount);

            LedgerAccount dispute = await GetOrCreateAccountAsync(tenantId, sellerId, LedgerAccountType.DISPUTE);

            var debitResult = source.Debit(amount, description, "DISPUTE_HOLD", transactionId);
            if (debitResult.IsFailure)
            {
                logger.LogWarning("Account {AccountType} balance {Balance} insufficient for dispute hold {Amount}. Seller: {SellerId}",
                    source.Type, source.Balance, amount, sellerId);
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);
            }

            var creditResult = dispute.Credit(amount, description, "DISPUTE_HOLD", transactionId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "HoldDispute");
    }

    public async Task ReleaseDisputeAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            // Double-entry: debit DISPUTE, credit WALLET (funds released back)
            LedgerAccount dispute = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.DISPUTE, sellerId)
                ?? throw new NotFoundException("LedgerAccount.DisputeNotFound", "Conta DISPUTE nao encontrada para este seller.");

            LedgerAccount wallet = await GetOrCreateAccountAsync(tenantId, sellerId, LedgerAccountType.WALLET);

            var debitResult = dispute.Debit(amount, description, "DISPUTE_RELEASE", transactionId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = wallet.Credit(amount, description, "DISPUTE_RELEASE", transactionId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "ReleaseDispute");
    }

    public async Task HoldDisputeFeeAsync(Guid tenantId, decimal feeAmount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            // L11: Freeze platform fee during dispute (Direct Charge).
            // Double-entry: debit PLATFORM_FEE → credit DISPUTE_FEE
            LedgerAccount platformFee = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_FEE)
                ?? throw new NotFoundException("LedgerAccount.PlatformFeeNotFound", "Conta PLATFORM_FEE nao encontrada.");

            LedgerAccount disputeFee = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.DISPUTE_FEE);

            var debitResult = platformFee.Debit(feeAmount, description, "DISPUTE_FEE_HOLD", transactionId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = disputeFee.Credit(feeAmount, description, "DISPUTE_FEE_HOLD", transactionId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "HoldDisputeFee");
    }

    public async Task ReleaseDisputeFeeAsync(Guid tenantId, decimal feeAmount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            // L11: Release frozen platform fee after dispute won (Direct Charge).
            // Double-entry: debit DISPUTE_FEE → credit PLATFORM_FEE
            LedgerAccount disputeFee = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.DISPUTE_FEE)
                ?? throw new NotFoundException("LedgerAccount.DisputeFeeNotFound", "Conta DISPUTE_FEE nao encontrada.");

            LedgerAccount platformFee = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_FEE);

            var debitResult = disputeFee.Debit(feeAmount, description, "DISPUTE_FEE_RELEASE", transactionId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = platformFee.Credit(feeAmount, description, "DISPUTE_FEE_RELEASE", transactionId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "ReleaseDisputeFee");
    }

    public async Task SettleDisputeLossAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            // H12: Dispute lost — zero out DISPUTE account.
            // Double-entry: debit DISPUTE (frozen funds released), credit PLATFORM_PAYOUT
            // (funds paid to cardholder via chargeback; platform absorbs the loss).
            LedgerAccount dispute = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.DISPUTE, sellerId)
                ?? throw new NotFoundException("LedgerAccount.DisputeNotFound", "Conta DISPUTE nao encontrada para este seller.");

            LedgerAccount platformPayout = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_PAYOUT);

            var debitResult = dispute.Debit(amount, description, "DISPUTE_LOSS", transactionId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = platformPayout.Credit(amount, description, "DISPUTE_LOSS", transactionId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "SettleDisputeLoss");
    }

    public async Task ReversePlatformFeeAsync(Guid tenantId, decimal feeAmount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount platformFee = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_FEE)
                ?? throw new NotFoundException("LedgerAccount.PlatformFeeNotFound", "Conta PLATFORM_FEE nao encontrada.");

            LedgerAccount platformPayout = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_PAYOUT);

            var debitResult = platformFee.Debit(feeAmount, description, "REFUND_FEE", transactionId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = platformPayout.Credit(feeAmount, description, "REFUND_FEE", transactionId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "ReversePlatformFee");
    }

    public async Task SettleDisputeFeeLossAsync(Guid tenantId, decimal feeAmount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            // L11: Dispute lost — zero out DISPUTE_FEE. Fee permanently lost.
            // Double-entry: debit DISPUTE_FEE → credit PLATFORM_PAYOUT
            LedgerAccount disputeFee = await ledgerRepository.GetPlatformAccountAsync(tenantId, LedgerAccountType.DISPUTE_FEE)
                ?? throw new NotFoundException("LedgerAccount.DisputeFeeNotFound", "Conta DISPUTE_FEE nao encontrada.");

            LedgerAccount platformPayout = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_PAYOUT);

            var debitResult = disputeFee.Debit(feeAmount, description, "DISPUTE_FEE_LOSS", transactionId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = platformPayout.Credit(feeAmount, description, "DISPUTE_FEE_LOSS", transactionId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "SettleDisputeFeeLoss");
    }

    public async Task DebitPayoutFeeAsync(Guid tenantId, Guid sellerId, decimal feeAmount, string description, string payoutId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount source = await FindFundedAccountAsync(tenantId, sellerId, feeAmount);
            LedgerAccount platformFee = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_FEE);

            var debitResult = source.Debit(feeAmount, description, "PAYOUT_FEE", payoutId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = platformFee.Credit(feeAmount, description, "PAYOUT_FEE", payoutId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "DebitPayoutFee");
    }

    public async Task ReversePayoutFeeAsync(Guid tenantId, Guid sellerId, decimal feeAmount, string description, string payoutId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount wallet = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.WALLET, sellerId)
                ?? throw new NotFoundException("LedgerAccount.WalletNotFound", "Conta WALLET nao encontrada para este seller.");

            LedgerAccount platformFee = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_FEE);

            var creditResult = wallet.Credit(feeAmount, description, "REVERSAL_FEE", payoutId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            var debitResult = platformFee.Debit(feeAmount, description, "REVERSAL_FEE", payoutId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            creditResult.Value.LinkContraEntry(debitResult.Value.Id);
            debitResult.Value.LinkContraEntry(creditResult.Value.Id);

            ledgerRepository.AddEntry(creditResult.Value);
            ledgerRepository.AddEntry(debitResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "ReversePayoutFee");
    }

    public async Task TransferBetweenSellersAsync(Guid tenantId, Guid fromSellerId, Guid toSellerId, decimal amount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            // Double-entry: debit source seller's WALLET, credit destination seller's WALLET
            LedgerAccount source = await FindFundedAccountAsync(tenantId, fromSellerId, amount);
            LedgerAccount destination = await GetOrCreateAccountAsync(tenantId, toSellerId, LedgerAccountType.WALLET);

            var debitResult = source.Debit(amount, description, "SPLIT_TRANSFER", transactionId);
            if (debitResult.IsFailure)
                throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = destination.Credit(amount, description, "SPLIT_TRANSFER", transactionId);
            if (creditResult.IsFailure)
                throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "TransferBetweenSellers");
    }

    public async Task CreditSplitClearingAsync(Guid tenantId, decimal amount, string description, string transactionId)
    {
        if (amount <= 0) return;

        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount platformAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_RECEIVABLE);
            LedgerAccount clearing = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.SPLIT_CLEARING);

            // 1) Credit PLATFORM_RECEIVABLE (external money in)
            var platformCredit = platformAccount.Credit(amount, $"Recebimento — {description}", "INCOMING_TRANSACTION", transactionId);
            if (platformCredit.IsFailure) throw new BusinessException(platformCredit.Error.Code, platformCredit.Error.Description);

            // 2) Debit PLATFORM_RECEIVABLE → Credit SPLIT_CLEARING (hold for distribution)
            var platformDebit = platformAccount.Debit(amount, $"Transfer to clearing — {description}", "SPLIT_CLEARING", transactionId);
            if (platformDebit.IsFailure) throw new BusinessException(platformDebit.Error.Code, platformDebit.Error.Description);

            var clearingCredit = clearing.Credit(amount, description, "SPLIT_CLEARING", transactionId);
            if (clearingCredit.IsFailure) throw new BusinessException(clearingCredit.Error.Code, clearingCredit.Error.Description);

            platformDebit.Value.LinkContraEntry(clearingCredit.Value.Id);
            clearingCredit.Value.LinkContraEntry(platformDebit.Value.Id);

            ledgerRepository.AddEntry(platformCredit.Value);
            ledgerRepository.AddEntry(platformDebit.Value);
            ledgerRepository.AddEntry(clearingCredit.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "CreditSplitClearing");
    }

    public async Task DistributeFromClearingAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId, string? idempotencyKey = null)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            if (idempotencyKey != null)
            {
                bool alreadyExists = await ledgerRepository.HasEntryWithReferenceAsync(tenantId, "SPLIT_DISTRIBUTE", idempotencyKey);
                if (alreadyExists)
                {
                    logger.LogWarning("DistributeFromClearing skipped (idempotent): key={Key} already has entries", idempotencyKey);
                    return 0;
                }
            }

            var refId = idempotencyKey ?? transactionId;

            LedgerAccount clearing = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.SPLIT_CLEARING);
            LedgerAccount sellerWallet = await GetOrCreateAccountAsync(tenantId, sellerId, LedgerAccountType.WALLET);

            var debitResult = clearing.Debit(amount, description, "SPLIT_DISTRIBUTE", refId);
            if (debitResult.IsFailure) throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = sellerWallet.Credit(amount, description, "SPLIT_DISTRIBUTE", refId);
            if (creditResult.IsFailure) throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "DistributeFromClearing");
    }

    public async Task ReturnToClearingAsync(Guid tenantId, Guid sellerId, decimal amount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount source = await FindFundedAccountAsync(tenantId, sellerId, amount);
            LedgerAccount clearing = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.SPLIT_CLEARING);

            var debitResult = source.Debit(amount, description, "SPLIT_RETURN", transactionId);
            if (debitResult.IsFailure) throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = clearing.Credit(amount, description, "SPLIT_RETURN", transactionId);
            if (creditResult.IsFailure) throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "ReturnToClearing");
    }

    public async Task DrainClearingForRefundAsync(Guid tenantId, decimal amount, string description, string transactionId)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount clearing = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.SPLIT_CLEARING);
            LedgerAccount platformPayout = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_PAYOUT);

            var debitResult = clearing.Debit(amount, description, "SPLIT_REFUND", transactionId);
            if (debitResult.IsFailure) throw new BusinessException(debitResult.Error.Code, debitResult.Error.Description);

            var creditResult = platformPayout.Credit(amount, description, "SPLIT_REFUND", transactionId);
            if (creditResult.IsFailure) throw new BusinessException(creditResult.Error.Code, creditResult.Error.Description);

            debitResult.Value.LinkContraEntry(creditResult.Value.Id);
            creditResult.Value.LinkContraEntry(debitResult.Value.Id);

            ledgerRepository.AddEntry(debitResult.Value);
            ledgerRepository.AddEntry(creditResult.Value);

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "DrainClearingForRefund");
    }

    public async Task ChargeAdvanceFeeAsync(Guid tenantId, Guid sellerId, decimal advanceFeeAmount, string description, string transactionId)
    {
        if (advanceFeeAmount <= 0) return;

        await ExecuteWithRetryAsync(async () =>
        {
            // Debita FUTURE_RECEIVABLES do seller — caso a antecipação rolou ANTES
            // do settlement (caso comum em ADVANCE: fee cobrado na captura, antes da
            // 1ª parcela liberar). Se já liberou (raro), cai pra WALLET.
            LedgerAccount? future = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.FUTURE_RECEIVABLES, sellerId)
                ?? throw new NotFoundException("LedgerAccount.FutureNotFound",
                    $"Seller {sellerId} sem FUTURE_RECEIVABLES — esperado pra cobrar advance fee na captura.");

            var sellerDebit = future.Debit(advanceFeeAmount, description, "ADVANCE_FEE", transactionId);
            if (sellerDebit.IsFailure)
                throw new BusinessException(sellerDebit.Error.Code, sellerDebit.Error.Description);

            LedgerAccount marginAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_MARGIN);
            var marginCredit = marginAccount.Credit(advanceFeeAmount, description, "ADVANCE_FEE", transactionId);
            if (marginCredit.IsFailure)
                throw new BusinessException(marginCredit.Error.Code, marginCredit.Error.Description);

            sellerDebit.Value.LinkContraEntry(marginCredit.Value.Id);
            marginCredit.Value.LinkContraEntry(sellerDebit.Value.Id);

            ledgerRepository.AddEntry(sellerDebit.Value);
            ledgerRepository.AddEntry(marginCredit.Value);

            await ledgerRepository.SaveChangesAsync();
            return true;
        }, "ChargeAdvanceFee");
    }

    public async Task ReverseAdvanceFeeAsync(Guid tenantId, Guid sellerId, decimal advanceFeeAmount, string description, string transactionId)
    {
        if (advanceFeeAmount <= 0) return;

        await ExecuteWithRetryAsync(async () =>
        {
            // ForceDebit em PLATFORM_MARGIN: pode ficar negativo se outras TXs já
            // consumiram a margem. Aceitável — vira lucro negativo do mês e é
            // surfaceado pelo reconciliation. Não bloqueamos o refund por isso.
            LedgerAccount marginAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_MARGIN);
            var marginDebit = marginAccount.ForceDebit(advanceFeeAmount, description, "ADVANCE_FEE_REVERSAL", transactionId);
            if (marginDebit.IsFailure)
                throw new BusinessException(marginDebit.Error.Code, marginDebit.Error.Description);

            // Credit volta no seller. Prefere FUTURE_RECEIVABLES (caso ADVANCE
            // ainda não settled, dinheiro está lá); fallback pra WALLET se já
            // moveu (dispute lost ocorre comumente após settlement).
            LedgerAccount? sellerAccount = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.FUTURE_RECEIVABLES, sellerId);
            if (sellerAccount == null || sellerAccount.Balance == 0m)
            {
                sellerAccount = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.WALLET, sellerId)
                    ?? throw new NotFoundException("LedgerAccount.SellerNotFound",
                        $"Seller {sellerId} sem WALLET nem FUTURE_RECEIVABLES — não pode reverter advance fee.");
            }

            var sellerCredit = sellerAccount.Credit(advanceFeeAmount, description, "ADVANCE_FEE_REVERSAL", transactionId);
            if (sellerCredit.IsFailure)
                throw new BusinessException(sellerCredit.Error.Code, sellerCredit.Error.Description);

            marginDebit.Value.LinkContraEntry(sellerCredit.Value.Id);
            sellerCredit.Value.LinkContraEntry(marginDebit.Value.Id);

            ledgerRepository.AddEntry(marginDebit.Value);
            ledgerRepository.AddEntry(sellerCredit.Value);

            await ledgerRepository.SaveChangesAsync();

            if (marginAccount.Balance < 0)
            {
                logger.LogCritical(
                    "[ADVANCE_REVERSAL] PLATFORM_MARGIN negativo após reversão de TX {TxId}: R${Balance}. " +
                    "Lucro do mês insuficiente pra cobrir reversões — operations review.",
                    transactionId, marginAccount.Balance);
            }

            return true;
        }, "ReverseAdvanceFee");
    }

    public async Task RecordPlatformMarginAsync(Guid tenantId, decimal platformFee, decimal providerCost, string description, string transactionId)
    {
        if (platformFee <= 0) return;

        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount feeAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_FEE);
            decimal margin = platformFee - providerCost;

            // 1) Credit PLATFORM_FEE with the gross platform fee (revenue recognition)
            var feeCredit = feeAccount.Credit(platformFee, $"Fee TX {transactionId}", "PLATFORM_FEE", transactionId);
            if (feeCredit.IsFailure)
                throw new BusinessException(feeCredit.Error.Code, feeCredit.Error.Description);
            ledgerRepository.AddEntry(feeCredit.Value);

            if (margin >= 0)
            {
                // Normal/zero margin: cost <= fee
                // 2) Debit PLATFORM_FEE by full cost → credit PROVIDER_COST
                if (providerCost > 0)
                {
                    LedgerAccount costAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PROVIDER_COST);

                    var feeDebitCost = feeAccount.Debit(providerCost, $"Provider cost TX {transactionId}", "PROVIDER_COST", transactionId);
                    if (feeDebitCost.IsFailure)
                        throw new BusinessException(feeDebitCost.Error.Code, feeDebitCost.Error.Description);

                    var costCredit = costAccount.Credit(providerCost, $"Provider cost TX {transactionId}", "PROVIDER_COST", transactionId);
                    if (costCredit.IsFailure)
                        throw new BusinessException(costCredit.Error.Code, costCredit.Error.Description);

                    feeDebitCost.Value.LinkContraEntry(costCredit.Value.Id);
                    costCredit.Value.LinkContraEntry(feeDebitCost.Value.Id);

                    ledgerRepository.AddEntry(feeDebitCost.Value);
                    ledgerRepository.AddEntry(costCredit.Value);
                }

                // 3) Remaining margin: debit PLATFORM_FEE → credit PLATFORM_MARGIN
                if (margin > 0)
                {
                    LedgerAccount marginAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_MARGIN);

                    var feeDebitMargin = feeAccount.Debit(margin, $"Margin TX {transactionId}", "PLATFORM_MARGIN", transactionId);
                    if (feeDebitMargin.IsFailure)
                        throw new BusinessException(feeDebitMargin.Error.Code, feeDebitMargin.Error.Description);

                    var marginCredit = marginAccount.Credit(margin, $"Margin TX {transactionId}", "PLATFORM_MARGIN", transactionId);
                    if (marginCredit.IsFailure)
                        throw new BusinessException(marginCredit.Error.Code, marginCredit.Error.Description);

                    feeDebitMargin.Value.LinkContraEntry(marginCredit.Value.Id);
                    marginCredit.Value.LinkContraEntry(feeDebitMargin.Value.Id);

                    ledgerRepository.AddEntry(feeDebitMargin.Value);
                    ledgerRepository.AddEntry(marginCredit.Value);
                }
            }
            else
            {
                // Negative margin: cost > fee — platform subsidizes the deficit
                decimal deficit = Math.Abs(margin);
                LedgerAccount costAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PROVIDER_COST);
                LedgerAccount marginAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_MARGIN);

                // 2a) Debit PLATFORM_FEE by platformFee (all available) → credit PROVIDER_COST
                var feeDebitCost = feeAccount.Debit(platformFee, $"Provider cost TX {transactionId}", "PROVIDER_COST", transactionId);
                if (feeDebitCost.IsFailure)
                    throw new BusinessException(feeDebitCost.Error.Code, feeDebitCost.Error.Description);

                var costCreditFromFee = costAccount.Credit(platformFee, $"Provider cost TX {transactionId} (from fee)", "PROVIDER_COST", transactionId);
                if (costCreditFromFee.IsFailure)
                    throw new BusinessException(costCreditFromFee.Error.Code, costCreditFromFee.Error.Description);

                feeDebitCost.Value.LinkContraEntry(costCreditFromFee.Value.Id);
                costCreditFromFee.Value.LinkContraEntry(feeDebitCost.Value.Id);

                ledgerRepository.AddEntry(feeDebitCost.Value);
                ledgerRepository.AddEntry(costCreditFromFee.Value);

                // 2b) ForceDebit PLATFORM_MARGIN by deficit → credit PROVIDER_COST (platform subsidy)
                // PLATFORM_MARGIN goes negative to reflect the loss (no balance check)
                var marginDebit = marginAccount.ForceDebit(deficit, $"Negative margin TX {transactionId}", "PLATFORM_MARGIN_DEFICIT", transactionId);
                if (marginDebit.IsFailure)
                    throw new BusinessException(marginDebit.Error.Code, marginDebit.Error.Description);

                var costCreditFromMargin = costAccount.Credit(deficit, $"Provider cost TX {transactionId} (subsidy)", "PLATFORM_MARGIN_DEFICIT", transactionId);
                if (costCreditFromMargin.IsFailure)
                    throw new BusinessException(costCreditFromMargin.Error.Code, costCreditFromMargin.Error.Description);

                marginDebit.Value.LinkContraEntry(costCreditFromMargin.Value.Id);
                costCreditFromMargin.Value.LinkContraEntry(marginDebit.Value.Id);

                ledgerRepository.AddEntry(marginDebit.Value);
                ledgerRepository.AddEntry(costCreditFromMargin.Value);
            }

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "RecordPlatformMargin");
    }

    public async Task ReversePlatformMarginAsync(Guid tenantId, decimal platformFee, decimal providerCost, string description, string transactionId)
    {
        if (platformFee <= 0) return;

        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount feeAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_FEE);
            decimal margin = platformFee - providerCost;

            // Reverse mirrors RecordPlatformMarginAsync.
            // PLATFORM_FEE is at 0 after recording, so we ForceDebit it (temporarily negative).
            // 1) ForceDebit PLATFORM_FEE (reverse the original credit)
            var feeDebit = feeAccount.ForceDebit(platformFee, $"Reversal fee {description}", "PLATFORM_FEE_REVERSAL", transactionId);
            if (feeDebit.IsFailure)
                throw new BusinessException(feeDebit.Error.Code, feeDebit.Error.Description);
            ledgerRepository.AddEntry(feeDebit.Value);

            if (margin >= 0)
            {
                // 2) Credit PLATFORM_FEE from PROVIDER_COST (reverse cost allocation)
                if (providerCost > 0)
                {
                    LedgerAccount costAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PROVIDER_COST);

                    var feeCreditCost = feeAccount.Credit(providerCost, $"Reversal provider cost {description}", "PROVIDER_COST_REVERSAL", transactionId);
                    if (feeCreditCost.IsFailure)
                        throw new BusinessException(feeCreditCost.Error.Code, feeCreditCost.Error.Description);

                    var costDebit = costAccount.Debit(providerCost, $"Reversal provider cost {description}", "PROVIDER_COST_REVERSAL", transactionId);
                    if (costDebit.IsFailure)
                        throw new BusinessException(costDebit.Error.Code, costDebit.Error.Description);

                    feeCreditCost.Value.LinkContraEntry(costDebit.Value.Id);
                    costDebit.Value.LinkContraEntry(feeCreditCost.Value.Id);

                    ledgerRepository.AddEntry(feeCreditCost.Value);
                    ledgerRepository.AddEntry(costDebit.Value);
                }

                // 3) Credit PLATFORM_FEE from PLATFORM_MARGIN (reverse margin allocation)
                if (margin > 0)
                {
                    LedgerAccount marginAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_MARGIN);

                    var feeCreditMargin = feeAccount.Credit(margin, $"Reversal margin {description}", "PLATFORM_MARGIN_REVERSAL", transactionId);
                    if (feeCreditMargin.IsFailure)
                        throw new BusinessException(feeCreditMargin.Error.Code, feeCreditMargin.Error.Description);

                    var marginDebit = marginAccount.Debit(margin, $"Reversal margin {description}", "PLATFORM_MARGIN_REVERSAL", transactionId);
                    if (marginDebit.IsFailure)
                        throw new BusinessException(marginDebit.Error.Code, marginDebit.Error.Description);

                    feeCreditMargin.Value.LinkContraEntry(marginDebit.Value.Id);
                    marginDebit.Value.LinkContraEntry(feeCreditMargin.Value.Id);

                    ledgerRepository.AddEntry(feeCreditMargin.Value);
                    ledgerRepository.AddEntry(marginDebit.Value);
                }
            }
            else
            {
                // Reverse negative margin: cost was split between fee and margin subsidy
                decimal deficit = Math.Abs(margin);
                LedgerAccount costAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PROVIDER_COST);
                LedgerAccount marginAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_MARGIN);

                // Credit PLATFORM_FEE by platformFee from PROVIDER_COST
                var feeCreditCost = feeAccount.Credit(platformFee, $"Reversal provider cost {description}", "PROVIDER_COST_REVERSAL", transactionId);
                if (feeCreditCost.IsFailure)
                    throw new BusinessException(feeCreditCost.Error.Code, feeCreditCost.Error.Description);

                var costDebitFee = costAccount.Debit(platformFee, $"Reversal provider cost {description}", "PROVIDER_COST_REVERSAL", transactionId);
                if (costDebitFee.IsFailure)
                    throw new BusinessException(costDebitFee.Error.Code, costDebitFee.Error.Description);

                feeCreditCost.Value.LinkContraEntry(costDebitFee.Value.Id);
                costDebitFee.Value.LinkContraEntry(feeCreditCost.Value.Id);

                ledgerRepository.AddEntry(feeCreditCost.Value);
                ledgerRepository.AddEntry(costDebitFee.Value);

                // Credit PLATFORM_MARGIN by deficit from PROVIDER_COST (reverse subsidy)
                var marginCredit = marginAccount.Credit(deficit, $"Reversal negative margin {description}", "PLATFORM_MARGIN_DEFICIT_REVERSAL", transactionId);
                if (marginCredit.IsFailure)
                    throw new BusinessException(marginCredit.Error.Code, marginCredit.Error.Description);

                var costDebitMargin = costAccount.Debit(deficit, $"Reversal subsidy {description}", "PLATFORM_MARGIN_DEFICIT_REVERSAL", transactionId);
                if (costDebitMargin.IsFailure)
                    throw new BusinessException(costDebitMargin.Error.Code, costDebitMargin.Error.Description);

                marginCredit.Value.LinkContraEntry(costDebitMargin.Value.Id);
                costDebitMargin.Value.LinkContraEntry(marginCredit.Value.Id);

                ledgerRepository.AddEntry(marginCredit.Value);
                ledgerRepository.AddEntry(costDebitMargin.Value);
            }

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "ReversePlatformMargin");
    }

    public async Task RecordCostAdjustmentAsync(Guid tenantId, decimal adjustment, string description, string transactionId)
    {
        if (adjustment == 0) return;

        await ExecuteWithRetryAsync(async () =>
        {
            LedgerAccount costAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PROVIDER_COST);
            LedgerAccount marginAccount = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.PLATFORM_MARGIN);

            if (adjustment > 0)
            {
                // Actual cost > estimated: increase PROVIDER_COST, decrease PLATFORM_MARGIN
                var costCredit = costAccount.Credit(adjustment, description, "COST_ADJUSTMENT", transactionId);
                if (costCredit.IsFailure) throw new BusinessException(costCredit.Error.Code, costCredit.Error.Description);

                var marginDebit = marginAccount.ForceDebit(adjustment, description, "COST_ADJUSTMENT", transactionId);
                if (marginDebit.IsFailure) throw new BusinessException(marginDebit.Error.Code, marginDebit.Error.Description);

                costCredit.Value.LinkContraEntry(marginDebit.Value.Id);
                marginDebit.Value.LinkContraEntry(costCredit.Value.Id);

                ledgerRepository.AddEntry(costCredit.Value);
                ledgerRepository.AddEntry(marginDebit.Value);
            }
            else
            {
                // Actual cost < estimated: decrease PROVIDER_COST, increase PLATFORM_MARGIN
                decimal absAdj = Math.Abs(adjustment);

                var costDebit = costAccount.Debit(absAdj, description, "COST_ADJUSTMENT", transactionId);
                if (costDebit.IsFailure) throw new BusinessException(costDebit.Error.Code, costDebit.Error.Description);

                var marginCredit = marginAccount.Credit(absAdj, description, "COST_ADJUSTMENT", transactionId);
                if (marginCredit.IsFailure) throw new BusinessException(marginCredit.Error.Code, marginCredit.Error.Description);

                costDebit.Value.LinkContraEntry(marginCredit.Value.Id);
                marginCredit.Value.LinkContraEntry(costDebit.Value.Id);

                ledgerRepository.AddEntry(costDebit.Value);
                ledgerRepository.AddEntry(marginCredit.Value);
            }

            await ledgerRepository.SaveChangesAsync();
            return 0;
        }, "RecordCostAdjustment");
    }

    public async Task<LedgerReconcileResult> ReconcileSellerBalanceAsync(
        Guid tenantId,
        Guid sellerId,
        decimal targetAvailable,
        decimal targetPending,
        string reason)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            // Carrega contas atuais — só ajusta se já existem, evita criar
            // conta nova só pra reconciliar (sinaliza problema mais grave).
            LedgerAccount? wallet = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.WALLET, sellerId);
            LedgerAccount? future = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.FUTURE_RECEIVABLES, sellerId);

            decimal currentAvailable = wallet?.Balance ?? 0;
            decimal currentPending = future?.Balance ?? 0;

            // Delta: positivo = local tem MAIS que o target → precisamos debitar.
            // Negativo = local tem MENOS que o target → precisamos creditar.
            decimal walletDelta = currentAvailable - targetAvailable;
            decimal futureDelta = currentPending - targetPending;

            if (walletDelta == 0 && futureDelta == 0)
            {
                logger.LogInformation(
                    "[LEDGER_RECONCILE] Seller {SellerId} já em sync (target {TgtAvail}/{TgtPending}). No-op.",
                    sellerId, targetAvailable, targetPending);
                return new LedgerReconcileResult(0, 0, 0, currentAvailable, currentPending);
            }

            // Conta de "contrapartida" — onde vai o ajuste contábil.
            // EXTERNAL_FUNDS semanticamente representa "valor que está fora do
            // ledger ativo" (ex: caixa Stripe da plataforma de TXs pré-Connect
            // que nunca foi transferido pro seller). Crédito aqui = "esse
            // dinheiro existe fora do nosso controle ativo".
            LedgerAccount external = await GetOrCreatePlatformAccountAsync(tenantId, LedgerAccountType.EXTERNAL_FUNDS);

            string description = $"Reconcile com fonte externa: {reason}";
            string referenceId = $"RECONCILE_{sellerId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            // Ajusta WALLET
            if (walletDelta != 0 && wallet != null)
            {
                if (walletDelta > 0)
                {
                    var debit = wallet.ForceDebit(walletDelta, description, "BALANCE_RECONCILE", referenceId);
                    if (debit.IsFailure) throw new BusinessException(debit.Error.Code, debit.Error.Description);

                    var credit = external.Credit(walletDelta, description, "BALANCE_RECONCILE", referenceId);
                    if (credit.IsFailure) throw new BusinessException(credit.Error.Code, credit.Error.Description);

                    debit.Value.LinkContraEntry(credit.Value.Id);
                    credit.Value.LinkContraEntry(debit.Value.Id);
                    ledgerRepository.AddEntry(debit.Value);
                    ledgerRepository.AddEntry(credit.Value);
                }
                else
                {
                    decimal abs = Math.Abs(walletDelta);
                    var credit = wallet.Credit(abs, description, "BALANCE_RECONCILE", referenceId);
                    if (credit.IsFailure) throw new BusinessException(credit.Error.Code, credit.Error.Description);

                    var debit = external.ForceDebit(abs, description, "BALANCE_RECONCILE", referenceId);
                    if (debit.IsFailure) throw new BusinessException(debit.Error.Code, debit.Error.Description);

                    credit.Value.LinkContraEntry(debit.Value.Id);
                    debit.Value.LinkContraEntry(credit.Value.Id);
                    ledgerRepository.AddEntry(credit.Value);
                    ledgerRepository.AddEntry(debit.Value);
                }
            }

            // Ajusta FUTURE_RECEIVABLES (mesmo padrão)
            if (futureDelta != 0 && future != null)
            {
                if (futureDelta > 0)
                {
                    var debit = future.ForceDebit(futureDelta, description, "BALANCE_RECONCILE", referenceId);
                    if (debit.IsFailure) throw new BusinessException(debit.Error.Code, debit.Error.Description);

                    var credit = external.Credit(futureDelta, description, "BALANCE_RECONCILE", referenceId);
                    if (credit.IsFailure) throw new BusinessException(credit.Error.Code, credit.Error.Description);

                    debit.Value.LinkContraEntry(credit.Value.Id);
                    credit.Value.LinkContraEntry(debit.Value.Id);
                    ledgerRepository.AddEntry(debit.Value);
                    ledgerRepository.AddEntry(credit.Value);
                }
                else
                {
                    decimal abs = Math.Abs(futureDelta);
                    var credit = future.Credit(abs, description, "BALANCE_RECONCILE", referenceId);
                    if (credit.IsFailure) throw new BusinessException(credit.Error.Code, credit.Error.Description);

                    var debit = external.ForceDebit(abs, description, "BALANCE_RECONCILE", referenceId);
                    if (debit.IsFailure) throw new BusinessException(debit.Error.Code, debit.Error.Description);

                    credit.Value.LinkContraEntry(debit.Value.Id);
                    debit.Value.LinkContraEntry(credit.Value.Id);
                    ledgerRepository.AddEntry(credit.Value);
                    ledgerRepository.AddEntry(debit.Value);
                }
            }

            await ledgerRepository.SaveChangesAsync();

            logger.LogWarning(
                "[LEDGER_RECONCILE] Seller {SellerId} ajustado: WALLET {WalletFrom}→{WalletTo} (Δ {WalletDelta}), FUTURE {FutureFrom}→{FutureTo} (Δ {FutureDelta}). Razão: {Reason}",
                sellerId,
                currentAvailable, targetAvailable, walletDelta,
                currentPending, targetPending, futureDelta,
                reason);

            return new LedgerReconcileResult(
                WalletAdjustment: walletDelta,
                FutureReceivablesAdjustment: futureDelta,
                TotalWriteOff: walletDelta + futureDelta,
                NewWalletBalance: targetAvailable,
                NewFutureReceivablesBalance: targetPending
            );
        }, "ReconcileSellerBalance");
    }

    private async Task<LedgerAccount> FindFundedAccountAsync(Guid tenantId, Guid sellerId, decimal amount)
    {
        LedgerAccount? wallet = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.WALLET, sellerId);
        if (wallet != null && wallet.Balance >= amount)
            return wallet;

        // Funds may still be in FUTURE_RECEIVABLES (pre-settlement)
        LedgerAccount? futureReceivables = await ledgerRepository.GetAccountAsync(tenantId, LedgerAccountType.FUTURE_RECEIVABLES, sellerId);
        if (futureReceivables != null && futureReceivables.Balance >= amount)
            return futureReceivables;

        // Return wallet (even if insufficient) so the caller gets a proper error from Debit()
        return wallet ?? throw new NotFoundException("LedgerAccount.WalletNotFound", "Conta WALLET nao encontrada para este seller.");
    }

    private async Task<LedgerAccount> GetOrCreateAccountAsync(Guid tenantId, Guid sellerId, LedgerAccountType type)
    {
        LedgerAccount? account = await ledgerRepository.GetAccountAsync(tenantId, type, sellerId);

        if (account == null)
        {
            account = LedgerAccount.Create(tenantId, sellerId, type);
            ledgerRepository.AddAccount(account);
        }

        return account;
    }

    private async Task<LedgerAccount> GetOrCreatePlatformAccountAsync(Guid tenantId, LedgerAccountType type)
    {
        LedgerAccount? account = await ledgerRepository.GetPlatformAccountAsync(tenantId, type);

        if (account == null)
        {
            account = LedgerAccount.Create(tenantId, null, type);
            ledgerRepository.AddAccount(account);
        }

        return account;
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string operationName)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (ConcurrencyException) when (attempt < MaxRetries)
            {
                logger.LogWarning(
                    "Concurrency conflict on LedgerAccount during {Operation}, attempt {Attempt}/{Max}. Retrying with fresh data.",
                    operationName, attempt, MaxRetries);

                // Exponential backoff with jitter to reduce contention
                var baseDelay = (int)Math.Pow(2, attempt) * 10; // 20, 40, 80, 160ms
                var jitter = Random.Shared.Next(0, baseDelay);
                await Task.Delay(baseDelay + jitter);
            }
        }

        // Final attempt — let it throw if it fails
        return await operation();
    }
}
